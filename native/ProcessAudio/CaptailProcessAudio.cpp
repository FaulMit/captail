/******************************************************************************
    Captail PID-aware process audio source.

    Process-loopback activation, format construction, silent-buffer handling,
    timestamps, and OBS audio delivery are narrowly adapted from OBS Studio
    32.1.2 plugins/win-wasapi/win-wasapi.cpp.

    OBS Studio copyright (C) Hugh Bailey and contributors.
    Captail adaptation copyright (C) Captail contributors.

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 2 of the License, or
    (at your option) any later version.
******************************************************************************/

#include <obs-module.h>

#include <Windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <avrt.h>
#include <ksmedia.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

constexpr const char *SourceId = "captail_process_audio_capture";
constexpr const char *TargetPidSetting = "target_pid";
constexpr const char *TargetCreationTimeSetting = "target_creation_time";
constexpr REFERENCE_TIME BufferTime100Ns = 5 * 10000000LL;
constexpr DWORD RetryDelayMilliseconds = 1000;

enum class SourceState : long long {
    Idle,
    Starting,
    Capturing,
    TargetExited,
    ActivationFailed,
    CaptureFailed,
    Stopped,
};

using ActivateAudioInterfaceAsyncFn = HRESULT(STDAPICALLTYPE *)(
    LPCWSTR,
    REFIID,
    PROPVARIANT *,
    IActivateAudioInterfaceCompletionHandler *,
    IActivateAudioInterfaceAsyncOperation **);

class ActivationHandler final
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::FtmBase,
          IActivateAudioInterfaceCompletionHandler> {
public:
    ActivationHandler() : event_(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {}

    ~ActivationHandler() override
    {
        if (event_)
            CloseHandle(event_);
    }

    HRESULT WaitForResult(IAudioClient **client)
    {
        if (!event_)
            return HRESULT_FROM_WIN32(GetLastError());
        WaitForSingleObject(event_, INFINITE);
        if (SUCCEEDED(result_) && activated_)
            return activated_.CopyTo(client);
        return result_;
    }

    HRESULT STDMETHODCALLTYPE ActivateCompleted(
        IActivateAudioInterfaceAsyncOperation *operation) override
    {
        HRESULT activationResult = E_FAIL;
        ComPtr<IUnknown> activated;
        HRESULT result = operation->GetActivateResult(
            &activationResult,
            activated.GetAddressOf());
        result_ = SUCCEEDED(result) ? activationResult : result;
        activated_ = std::move(activated);
        if (event_)
            SetEvent(event_);
        return S_OK;
    }

private:
    HANDLE event_ = nullptr;
    HRESULT result_ = E_PENDING;
    ComPtr<IUnknown> activated_;
};

DWORD SpeakerChannelMask(speaker_layout speakers)
{
    switch (speakers) {
    case SPEAKERS_MONO:
        return KSAUDIO_SPEAKER_MONO;
    case SPEAKERS_STEREO:
        return KSAUDIO_SPEAKER_STEREO;
    case SPEAKERS_2POINT1:
        return KSAUDIO_SPEAKER_2POINT1;
    case SPEAKERS_4POINT0:
        return KSAUDIO_SPEAKER_SURROUND;
    case SPEAKERS_4POINT1:
        return KSAUDIO_SPEAKER_SURROUND | SPEAKER_LOW_FREQUENCY;
    case SPEAKERS_5POINT1:
        return KSAUDIO_SPEAKER_5POINT1_SURROUND;
    case SPEAKERS_7POINT1:
        return KSAUDIO_SPEAKER_7POINT1_SURROUND;
    default:
        return 0;
    }
}

uint64_t FileTimeValue(const FILETIME &time)
{
    ULARGE_INTEGER value{};
    value.LowPart = time.dwLowDateTime;
    value.HighPart = time.dwHighDateTime;
    return value.QuadPart;
}

class ProcessAudioSource {
public:
    ProcessAudioSource(obs_data_t *settings, obs_source_t *source)
        : source_(source)
    {
        const long long processId = obs_data_get_int(settings, TargetPidSetting);
        const long long creationTime =
            obs_data_get_int(settings, TargetCreationTimeSetting);
        if (!source_ || processId <= 0 || processId > MAXDWORD || creationTime <= 0)
            throw E_INVALIDARG;

        processId_ = static_cast<DWORD>(processId);
        creationTime_ = static_cast<uint64_t>(creationTime);
        try {
            stopEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!stopEvent_)
                throw HRESULT_FROM_WIN32(GetLastError());

            process_ = OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
                FALSE,
                processId_);
            if (!process_)
                throw HRESULT_FROM_WIN32(GetLastError());
            if (!IdentityMatches())
                throw HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        } catch (...) {
            if (process_)
                CloseHandle(process_);
            if (stopEvent_)
                CloseHandle(stopEvent_);
            process_ = nullptr;
            stopEvent_ = nullptr;
            throw;
        }
    }

    ~ProcessAudioSource()
    {
        Stop();
        if (process_)
            CloseHandle(process_);
        if (stopEvent_)
            CloseHandle(stopEvent_);
    }

    void Start()
    {
        std::lock_guard lock(workerMutex_);
        if (worker_.joinable())
            return;
        ResetEvent(stopEvent_);
        state_ = SourceState::Starting;
        error_ = S_OK;
        worker_ = std::thread(&ProcessAudioSource::Run, this);
    }

    void Stop()
    {
        std::thread worker;
        {
            std::lock_guard lock(workerMutex_);
            if (!worker_.joinable()) {
                state_ = SourceState::Stopped;
                return;
            }
            SetEvent(stopEvent_);
            worker = std::move(worker_);
        }
        worker.join();
        state_ = SourceState::Stopped;
    }

    SourceState State() const { return state_.load(); }
    HRESULT Error() const { return error_.load(); }

private:
    enum class CaptureResult {
        Stopped,
        TargetExited,
        Failed,
    };

    bool IdentityMatches() const
    {
        if (!process_ || WaitForSingleObject(process_, 0) == WAIT_OBJECT_0)
            return false;
        FILETIME creation{}, exit{}, kernel{}, user{};
        return GetProcessTimes(process_, &creation, &exit, &kernel, &user) &&
               FileTimeValue(creation) == creationTime_;
    }

    void Run()
    {
        const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const bool comInitialized = SUCCEEDED(comResult);
        DWORD taskIndex = 0;
        HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);

        while (WaitForSingleObject(stopEvent_, 0) != WAIT_OBJECT_0) {
            if (!IdentityMatches()) {
                state_ = SourceState::TargetExited;
                break;
            }

            bool activationFailed = false;
            HRESULT failure = S_OK;
            CaptureResult result = CaptureOnce(activationFailed, failure);
            if (result == CaptureResult::Stopped)
                break;
            if (result == CaptureResult::TargetExited) {
                state_ = SourceState::TargetExited;
                break;
            }

            state_ = activationFailed
                ? SourceState::ActivationFailed
                : SourceState::CaptureFailed;
            error_ = failure;
            HANDLE retryHandles[] = {stopEvent_, process_};
            DWORD wait = WaitForMultipleObjects(
                2,
                retryHandles,
                FALSE,
                RetryDelayMilliseconds);
            if (wait == WAIT_OBJECT_0)
                break;
            if (wait == WAIT_OBJECT_0 + 1) {
                state_ = SourceState::TargetExited;
                break;
            }
        }

        if (mmcss)
            AvRevertMmThreadCharacteristics(mmcss);
        if (comInitialized)
            CoUninitialize();
    }

    CaptureResult CaptureOnce(bool &activationFailed, HRESULT &failure)
    {
        activationFailed = true;
        HMODULE mmdevapi = GetModuleHandleW(L"Mmdevapi.dll");
        if (!mmdevapi)
            mmdevapi = LoadLibraryW(L"Mmdevapi.dll");
        if (!mmdevapi) {
            failure = HRESULT_FROM_WIN32(GetLastError());
            return CaptureResult::Failed;
        }

        auto activate = reinterpret_cast<ActivateAudioInterfaceAsyncFn>(
            GetProcAddress(mmdevapi, "ActivateAudioInterfaceAsync"));
        if (!activate) {
            failure = HRESULT_FROM_WIN32(GetLastError());
            return CaptureResult::Failed;
        }

        obs_audio_info audioInfo{};
        if (!obs_get_audio_info(&audioInfo)) {
            failure = E_FAIL;
            return CaptureResult::Failed;
        }

        WAVEFORMATEXTENSIBLE wave{};
        const WORD channels = static_cast<WORD>(get_audio_channels(audioInfo.speakers));
        if (channels == 0) {
            failure = E_INVALIDARG;
            return CaptureResult::Failed;
        }
        constexpr WORD bitsPerSample = 32;
        const WORD blockAlign = channels * bitsPerSample / 8;
        wave.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
        wave.Format.nChannels = channels;
        wave.Format.nSamplesPerSec = audioInfo.samples_per_sec;
        wave.Format.nAvgBytesPerSec = audioInfo.samples_per_sec * blockAlign;
        wave.Format.nBlockAlign = blockAlign;
        wave.Format.wBitsPerSample = bitsPerSample;
        wave.Format.cbSize = sizeof(wave) - sizeof(wave.Format);
        wave.Samples.wValidBitsPerSample = bitsPerSample;
        wave.dwChannelMask = SpeakerChannelMask(audioInfo.speakers);
        wave.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;

        AUDIOCLIENT_ACTIVATION_PARAMS activationParams{};
        activationParams.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        activationParams.ProcessLoopbackParams.TargetProcessId = processId_;
        activationParams.ProcessLoopbackParams.ProcessLoopbackMode =
            PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

        PROPVARIANT parameters{};
        parameters.vt = VT_BLOB;
        parameters.blob.cbSize = sizeof(activationParams);
        parameters.blob.pBlobData = reinterpret_cast<BYTE *>(&activationParams);

        ComPtr<ActivationHandler> handler = Microsoft::WRL::Make<ActivationHandler>();
        if (!handler) {
            failure = E_OUTOFMEMORY;
            return CaptureResult::Failed;
        }
        ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
        HRESULT result = activate(
            VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            __uuidof(IAudioClient),
            &parameters,
            handler.Get(),
            operation.GetAddressOf());
        if (FAILED(result)) {
            failure = result;
            return CaptureResult::Failed;
        }

        ComPtr<IAudioClient> client;
        result = handler->WaitForResult(client.GetAddressOf());
        if (FAILED(result)) {
            failure = result;
            return CaptureResult::Failed;
        }
        if (!IdentityMatches())
            return CaptureResult::TargetExited;

        result = client->Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_LOOPBACK,
            BufferTime100Ns,
            0,
            &wave.Format,
            nullptr);
        if (FAILED(result)) {
            failure = result;
            return CaptureResult::Failed;
        }

        HANDLE sampleEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!sampleEvent) {
            failure = HRESULT_FROM_WIN32(GetLastError());
            return CaptureResult::Failed;
        }

        ComPtr<IAudioCaptureClient> capture;
        result = client->GetService(IID_PPV_ARGS(capture.GetAddressOf()));
        if (SUCCEEDED(result))
            result = client->SetEventHandle(sampleEvent);
        if (SUCCEEDED(result))
            result = client->Start();
        if (FAILED(result)) {
            CloseHandle(sampleEvent);
            failure = result;
            return CaptureResult::Failed;
        }

        activationFailed = false;
        state_ = SourceState::Capturing;
        error_ = S_OK;
        HANDLE handles[] = {stopEvent_, process_, sampleEvent};
        CaptureResult captureResult = CaptureResult::Failed;
        while (true) {
            DWORD wait = WaitForMultipleObjects(3, handles, FALSE, INFINITE);
            if (wait == WAIT_OBJECT_0) {
                captureResult = CaptureResult::Stopped;
                break;
            }
            if (wait == WAIT_OBJECT_0 + 1) {
                captureResult = CaptureResult::TargetExited;
                break;
            }
            if (wait != WAIT_OBJECT_0 + 2) {
                failure = HRESULT_FROM_WIN32(GetLastError());
                break;
            }
            if (!OutputPackets(capture.Get(), audioInfo)) {
                failure = lastCaptureError_;
                break;
            }
        }

        client->Stop();
        CloseHandle(sampleEvent);
        return captureResult;
    }

    bool OutputPackets(IAudioCaptureClient *capture, const obs_audio_info &audioInfo)
    {
        UINT32 packetFrames = 0;
        HRESULT result = capture->GetNextPacketSize(&packetFrames);
        while (SUCCEEDED(result) && packetFrames > 0) {
            BYTE *buffer = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            UINT64 devicePosition = 0;
            UINT64 timestamp = 0;
            result = capture->GetBuffer(
                &buffer,
                &frames,
                &flags,
                &devicePosition,
                &timestamp);
            if (FAILED(result))
                break;

            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) {
                size_t required = static_cast<size_t>(
                    get_audio_channels(audioInfo.speakers)) * frames * sizeof(float);
                if (silence_.size() < required)
                    silence_.resize(required, 0);
                buffer = silence_.data();
            }

            obs_source_audio audio{};
            audio.data[0] = buffer;
            audio.frames = frames;
            audio.speakers = audioInfo.speakers;
            audio.samples_per_sec = audioInfo.samples_per_sec;
            audio.format = AUDIO_FORMAT_FLOAT;
            audio.timestamp = timestamp * 100;
            obs_source_output_audio(source_, &audio);
            capture->ReleaseBuffer(frames);

            result = capture->GetNextPacketSize(&packetFrames);
        }

        if (FAILED(result)) {
            lastCaptureError_ = result;
            return false;
        }
        return true;
    }

    obs_source_t *source_ = nullptr;
    DWORD processId_ = 0;
    uint64_t creationTime_ = 0;
    HANDLE process_ = nullptr;
    HANDLE stopEvent_ = nullptr;
    std::atomic<SourceState> state_{SourceState::Idle};
    std::atomic<HRESULT> error_{S_OK};
    HRESULT lastCaptureError_ = S_OK;
    std::mutex workerMutex_;
    std::thread worker_;
    std::vector<BYTE> silence_;
};

const char *GetSourceName(void *)
{
    return "Captail process audio";
}

void GetStatus(void *data, calldata_t *callData)
{
    auto *source = static_cast<ProcessAudioSource *>(data);
    if (!source)
        return;
    const long long state = static_cast<long long>(source->State());
    const long long errorCode = source->Error();
    calldata_set_data(callData, "state", &state, sizeof(state));
    calldata_set_data(
        callData,
        "error_code",
        &errorCode,
        sizeof(errorCode));
}

void *CreateSource(obs_data_t *settings, obs_source_t *source)
{
    try {
        auto *processSource = new ProcessAudioSource(settings, source);
        proc_handler_t *handler = obs_source_get_proc_handler(source);
        if (handler) {
            proc_handler_add(
                handler,
                "void get_status(out int state, out int error_code)",
                GetStatus,
                processSource);
        }
        return processSource;
    } catch (...) {
        return nullptr;
    }
}

void DestroySource(void *data)
{
    delete static_cast<ProcessAudioSource *>(data);
}

void ActivateSource(void *data)
{
    auto *source = static_cast<ProcessAudioSource *>(data);
    if (source)
        source->Start();
}

void DeactivateSource(void *data)
{
    auto *source = static_cast<ProcessAudioSource *>(data);
    if (source)
        source->Stop();
}

} // namespace

OBS_DECLARE_MODULE()

MODULE_EXPORT const char *obs_module_description(void)
{
    return "Captail PID-aware Windows process audio source";
}

bool obs_module_load(void)
{
    obs_source_info info{};
    info.id = SourceId;
    info.type = OBS_SOURCE_TYPE_INPUT;
    info.output_flags = OBS_SOURCE_AUDIO |
                        OBS_SOURCE_DO_NOT_DUPLICATE |
                        OBS_SOURCE_DO_NOT_SELF_MONITOR;
    info.get_name = GetSourceName;
    info.create = CreateSource;
    info.destroy = DestroySource;
    info.activate = ActivateSource;
    info.deactivate = DeactivateSource;
    info.icon_type = OBS_ICON_TYPE_PROCESS_AUDIO_OUTPUT;
    obs_register_source(&info);
    return true;
}
