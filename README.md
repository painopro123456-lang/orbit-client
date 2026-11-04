# ORBIT

A premium Windows desktop launcher for Minecraft: Java Edition.

## Working in this build
- Microsoft account sign-in through MSAL and the Xbox/Minecraft authentication flow.
- Minecraft Java profile and entitlement validation during sign-in.
- Persistent account/token cache in the user's local ORBIT data directory.
- Live Minecraft version manifest loading, with 26.2 selected automatically when Mojang lists it.
- Vanilla version download/repair and launch through CmlLib.Core.
- Download progress and Minecraft process status in the launcher.
- Self-contained Windows x64 build.

## Microsoft app registration
ORBIT uses the public client ID configured in `MainWindow.xaml.cs`. The Microsoft Entra app must:
1. Support personal Microsoft accounts (or accounts in any organization plus personal Microsoft accounts).
2. Be configured as a public client / mobile and desktop application.
3. Include the exact redirect URI `http://localhost:53682/callback` under Mobile and desktop applications.
4. Permit the Xbox Live sign-in scope used by the Minecraft authentication flow.

The client ID is a public identifier, not a client secret. ORBIT never asks the user to type their Microsoft password into the launcher; sign-in is handled by Microsoft's system-browser flow.

## Build
Requires the .NET 10 SDK on Windows 10/11.

```powershell
dotnet publish Orbit/Orbit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Current limitations
This milestone implements Microsoft authentication and vanilla Minecraft installation/launch. Separate instance management, Fabric/Forge/NeoForge/Quilt installers, Modrinth/CurseForge integration, and automatic launcher updates are still pending. Compatibility with the newest Minecraft releases depends on the metadata and runtime support in the current CmlLib release.
