---
description: Build OmenSuperHub WPF project
---
// turbo-all

1. Set SDK path and disable workload resolver:
```cmd
set MSBuildSDKsPath=C:\Program Files\dotnet\sdk\8.0.418\Sdks
set MSBuildEnableWorkloadResolver=false
```

2. Build with VS MSBuild:
```cmd
"D:\Runtime\MVS2022\MSBuild\Current\Bin\amd64\MSBuild.exe" OmenSuperHub.csproj /t:Build /p:Configuration=Release /p:Platform=x64
```

3. Output will be at: `bin\x64\Release\OmenSuperHub.exe`

**Note:** If LibreHMLib needs NuGet restore first:
```cmd
dotnet restore OmenSuperHub.csproj
```
