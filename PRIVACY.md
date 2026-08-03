# Captail Privacy Policy

Effective date: August 3, 2026

This policy applies to the Captail desktop application and its official
Microsoft Store and GitHub distributions.

## Summary

Captail records instant replays locally on your Windows PC. Captail does not
sell personal information, include advertising or analytics, automatically
upload recordings, or provide the developer with access to your captures.

## Information processed locally

Depending on the features you enable, Captail may process:

- desktop, application, or game video frames;
- desktop or game audio;
- microphone audio;
- monitor, audio-device, GPU, codec, and game-process information needed to
  configure capture;
- replay files and their technical metadata;
- settings such as hotkeys, selected devices, and output folders.

This information is processed on your device to maintain the replay buffer,
save clips, organize recordings, show previews, and trim saved videos. Captail
does not transmit captured video or audio to the developer or to a Captail
cloud service.

Captail does not attempt to bypass DRM or protected-media restrictions.
Protected content may appear black or unavailable in recordings.

## Local storage

Saved replays are written to the folder you select. The rolling replay buffer
is held locally and only becomes a normal video file when you save a replay.

Captail stores configuration and diagnostic logs under its local application
data. The logical log location is:

```text
%LOCALAPPDATA%\Captail\log.txt
```

Windows may map Store-package application data to a package-specific location.
Logs can contain technical errors, device and GPU names, codec settings, game
or process names, and local file paths. Logs do not intentionally contain raw
video frames or recorded audio. Logs are not uploaded automatically. Review
them and remove sensitive paths or names before attaching them to a public bug
report.

## Network access

Captail has no telemetry, analytics, advertising, account, or cloud-storage
service.

The Microsoft Store build relies on Microsoft Store for application updates
and does not check GitHub Releases for updates. Microsoft Store may process
installation, licensing, update, and diagnostic information under Microsoft's
own privacy terms.

Installer and Portable builds distributed through GitHub may contact the
GitHub Releases API to check for and download updates. Those requests identify
the Captail version and necessarily expose network information such as the IP
address to GitHub. GitHub processes that information under the
[GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

Opening a GitHub, support, or repository link from Captail launches your
default web browser. The destination website then applies its own privacy
policy.

## Permissions

Captail uses only permissions needed for its recording workflow, including:

- screen and game capture;
- system-audio and optional microphone capture;
- global replay hotkeys;
- access to folders selected for replay storage;
- hardware encoder detection;
- optional startup with Windows.

Microphone recording and startup with Windows can be disabled in Captail's
settings. Capture can be stopped at any time by turning Instant Replay off or
exiting Captail.

## Sharing and disclosure

Captail does not sell or share local recordings, settings, or diagnostic logs.
Information leaves your device only when you choose to share a replay or log,
or when a non-Store build performs the GitHub update request described above.

## Retention and deletion

You control saved replay retention. Replays can be deleted from Captail or
Windows File Explorer. Configuration and logs remain locally until you delete
them or Windows removes the application's data. Uninstalling Captail does not
guarantee removal of replay files stored in your chosen output folder.

## Children's privacy

Captail is a general-purpose recording utility and is not directed to children.
The developer does not knowingly collect personal information from children.

## Changes to this policy

Material changes will be published in this file and recorded in the
repository's Git history. The effective date above will be updated when the
policy changes.

## Contact

For privacy questions, open an issue in the
[Captail repository](https://github.com/FaulMit/captail/issues). Do not include
recordings, logs, local paths, or other sensitive information in a public
issue.
