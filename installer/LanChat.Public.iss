#if !FileExists("PublicPublishDir.local.iss")
  #error Publish LanChat.WinForms using the PublicRelease profile before compiling this installer.
#endif
#include "PublicPublishDir.local.iss"
#ifndef PublishDir
  #error The public publish configuration is missing PublishDir.
#endif
#ifndef AppVersion
  #error The public publish configuration is missing AppVersion.
#endif
#define SetupOutputName "LanChat-Setup-Windows-x64"
#define SetupOutputDirectory "..\artifacts\public-release\installer"
#include "LanChat.iss"
