#:project ../src/EchoForge.Audio.Windows/EchoForge.Audio.Windows.csproj
#:property TargetFramework=net10.0-windows
#:property BuiltInComInteropSupport=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false

// What the real endpoint catalogue actually reports on this machine, and what the recorder's
// stored settings would do with it. Diagnostic only: it opens nothing and records nothing.
//
//   dotnet run scripts/probe-devices.cs

using System.Text.Json;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;

AudioDeviceCatalog catalog = new();

Console.WriteLine("Render endpoints");
foreach (AudioEndpointInfo endpoint in catalog.GetRenderEndpoints())
{
    Console.WriteLine($"  {(endpoint.IsDefault ? "*" : " ")} {endpoint.FriendlyName}  [{endpoint.Id}]  {endpoint.MixFormat}");
}

Console.WriteLine();
Console.WriteLine("Capture endpoints");
foreach (AudioEndpointInfo endpoint in catalog.GetCaptureEndpoints())
{
    Console.WriteLine($"  {(endpoint.IsDefault ? "*" : " ")} {endpoint.FriendlyName}  [{endpoint.Id}]  {endpoint.MixFormat}");
}

string settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "EchoForge", "config", "settings.json");

Console.WriteLine();
Console.WriteLine("Stored settings  " + settingsPath);
Console.WriteLine(File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "  (none)");
