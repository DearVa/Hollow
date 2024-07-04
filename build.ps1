param(
    [string] $Version = "0.0.1.0"
)

$ErrorActionPreference = "Stop";

Write-Output "Start building withRuntime...";

dotnet publish Hollow/Hollow.csproj -o "build/$Version/withRuntime" -p:Platform=$Architecture -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:SelfContained=true -p:AssemblyVersion=$Version -p:Configuration=Release;

Rename-Item -Path "build/$Version/withRuntime/Hollow.exe" -NewName "Hollow_withRuntime.exe"

Write-Output "Start building withoutRuntime...";

dotnet publish Hollow/Hollow.csproj -o "build/$Version/withoutRuntime" -p:Platform=$Architecture -p:PublishReadyToRun=false -p:PublishSingleFile=true -p:SelfContained=false -p:AssemblyVersion=$Version -p:Configuration=Release;

Write-Output "Build Finished";

[Console]::ReadKey()