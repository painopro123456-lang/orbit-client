# ORBIT

A premium Windows desktop launcher for Minecraft: Java Edition.

## Current status
ORBIT is in its initial UI foundation stage. The current build contains the branded WPF desktop shell, custom window chrome, navigation, overview dashboard and a Windows x64 publishing workflow.

## Planned integrations
- Microsoft account authentication and Minecraft entitlement checks
- Minecraft version installation and launching
- Separate instances and per-instance settings
- Fabric, Forge, NeoForge and Quilt
- Modrinth and CurseForge discovery and modpack installation
- Java and memory configuration
- Automatic updates

## Build
Requires the .NET 10 SDK on Windows 10/11.

```powershell
dotnet publish Orbit/Orbit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The GitHub Actions workflow builds a self-contained Windows x64 executable and uploads it as an artifact.

## Important
The first preview is a visual and navigation foundation. Authentication, downloading, instance management and game launching are not implemented yet.