cmake_minimum_required(VERSION 3.20)
project(CaptailObsWinrt LANGUAGES CXX)

if(NOT DEFINED OBS_INCLUDE_DIR OR NOT EXISTS "${OBS_INCLUDE_DIR}/obs-module.h")
  message(FATAL_ERROR "OBS_INCLUDE_DIR must point to the pinned libobs public headers")
endif()

add_library(CaptailObsWinrt MODULE winrt-capture.cpp winrt-dispatch.cpp)
set(OBS_IMPORT_LIBRARY "${CMAKE_CURRENT_BINARY_DIR}/obs.lib")
add_custom_command(
  OUTPUT "${OBS_IMPORT_LIBRARY}"
  COMMAND "${CMAKE_AR}" /nologo "/def:${CMAKE_CURRENT_SOURCE_DIR}/Obs.def"
    /machine:x64 "/out:${OBS_IMPORT_LIBRARY}"
  DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/Obs.def"
  VERBATIM)
add_custom_target(CaptailObsWinrtImportLibrary DEPENDS "${OBS_IMPORT_LIBRARY}")
add_dependencies(CaptailObsWinrt CaptailObsWinrtImportLibrary)

target_compile_features(CaptailObsWinrt PRIVATE cxx_std_20)
target_compile_definitions(CaptailObsWinrt PRIVATE NOMINMAX UNICODE _UNICODE _WIN32_WINNT=0x0A00)
target_include_directories(CaptailObsWinrt SYSTEM PRIVATE "${OBS_INCLUDE_DIR}")
target_precompile_headers(CaptailObsWinrt PRIVATE
  <d3d11.h> <DispatcherQueue.h> <dwmapi.h> <obs-module.h>
  <util/windows/ComPtr.hpp> <Windows.Graphics.Capture.Interop.h>
  <windows.graphics.directx.direct3d11.interop.h>
  <winrt/Windows.Foundation.Metadata.h> <winrt/Windows.Graphics.Capture.h>
  <winrt/Windows.System.h>)
target_link_libraries(CaptailObsWinrt PRIVATE "${OBS_IMPORT_LIBRARY}" dwmapi windowsapp d3d11 CoreMessaging)
if(MSVC)
  target_compile_options(CaptailObsWinrt PRIVATE /W4 /permissive- /EHsc)
  set_property(TARGET CaptailObsWinrt PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
endif()
set_target_properties(CaptailObsWinrt PROPERTIES OUTPUT_NAME "libobs-winrt" PREFIX "")
