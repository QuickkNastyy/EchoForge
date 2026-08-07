using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Setup;

namespace EchoForge.UnitTests;

/// <summary>
/// A manifest, a fake origin server, and a throwaway layout.
///
/// <para>
/// The artifacts are small files built here, not the real 1.6 GB model: what these tests are about
/// is the installation machinery — verification, resumption, atomic activation, repair — and that
/// machinery does not care how big a file is. The one thing kept real is the shape: an archive
/// that really is a gzipped tar with a <c>python/</c> prefix, because the extractor's handling of
/// that prefix is exactly the sort of thing a mock would paper over.
/// </para>
/// </summary>
public sealed class SetupFixture : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly Dictionary<string, byte[]> _origin = new(StringComparer.Ordinal);
    private readonly List<ArtifactEntry> _entries = [];

    public SetupFixture()
    {
        Layout = AppLayout.For(_temp.Combine("app"), _temp.Combine("data"));
        Directory.CreateDirectory(Layout.ApplicationRoot);
        Layout.EnsureDataDirectories();
    }

    public AppLayout Layout { get; }

    /// <summary>Requests the fake origin refused, so a test can make a download fail.</summary>
    public HashSet<string> Blocked { get; } = new(StringComparer.Ordinal);

    /// <summary>Bytes the origin serves instead of the real ones, to forge a digest mismatch.</summary>
    public Dictionary<string, byte[]> Substituted { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times each URL was asked for. Proves resumption rather than assuming it.</summary>
    public Dictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);

    // -- building a manifest -----------------------------------------------------------------------

    /// <summary>Adds an artifact whose content is exactly the bytes given.</summary>
    public ArtifactEntry Add(
        string artifactId,
        string fileName,
        byte[] content,
        string kind = "runtime",
        params string[] profiles)
    {
        string url = "https://origin.invalid/" + artifactId + "/" + fileName;
        _origin[url] = content;

        ArtifactEntry entry = new()
        {
            ArtifactId = artifactId,
            Kind = kind,
            Repository = "https://origin.invalid/" + artifactId,
            Url = url,
            Revision = "rev-0000001",
            FileName = fileName,
            SizeBytes = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            License = "MIT",
            LicenseFile = "third_party/licenses/test-LICENSE.txt",
            RuntimeVersion = "test",
            Profiles = profiles.Length > 0 ? profiles : ["any"],
            VerifiedUtc = new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero),
        };

        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Adds an interpreter archive with the layout python-build-standalone actually publishes:
    /// a gzipped tar whose every entry sits under <c>python/</c>.
    /// </summary>
    public ArtifactEntry AddPythonArchive(string artifactId = PythonRuntimeInstaller.ArtifactId, bool includeExecutable = true)
    {
        using MemoryStream raw = new();

        using (GZipStream gzip = new(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            if (includeExecutable)
            {
                WriteEntry(writer, "python/python.exe", "not really an interpreter");
            }

            WriteEntry(writer, "python/LICENSE.txt", "PSF");
            WriteEntry(writer, "python/Lib/os.py", "import sys");
            WriteEntry(writer, "python/Scripts/pip.exe", "pip");
        }

        ArtifactEntry entry = Add(
            artifactId,
            "cpython-test-install_only.tar.gz",
            raw.ToArray());

        // The installer reads the version out of the pinned entry rather than by running anything.
        ArtifactEntry described = entry with { RuntimeVersion = "CPython 3.12.13 (test build)" };
        _entries[_entries.IndexOf(entry)] = described;
        return described;
    }

    private static void WriteEntry(TarWriter writer, string name, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        PaxTarEntry entry = new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) };
        writer.WriteEntry(entry);
    }

    /// <summary>Adds a wheel-shaped artifact so the worker environment has a closure to install.</summary>
    public ArtifactEntry AddWheel(string package, string version = "1.0.0")
    {
        return Add(
            "runtime." + package,
            $"{package}-{version}-py3-none-any.whl",
            Encoding.UTF8.GetBytes($"wheel:{package}:{version}"));
    }

    /// <summary>Builds a registry over the manifest assembled so far.</summary>
    public ArtifactRegistry Registry() => new(
        new ArtifactManifest { SchemaVersion = 1, Artifacts = [.. _entries] },
        Layout.ModelsRoot,
        new OriginHandler(this));

    public PythonRuntimeInstaller PythonInstaller(ArtifactRegistry registry) => new(registry, Layout);

    public WorkerEnvironmentInstaller WorkerInstaller(ArtifactRegistry registry, PythonRuntimeInstaller python) =>
        new(registry, python, Layout);

    /// <summary>Writes the package list the worker environment installs from.</summary>
    public void WriteRequirements(string content = "example==1.0.0\n")
    {
        Directory.CreateDirectory(Layout.WorkerPackageRoot);
        File.WriteAllText(Path.Combine(Layout.WorkerPackageRoot, "requirements-production.txt"), content);
    }

    /// <summary>Where an artifact's bytes land, for a test that wants to damage them.</summary>
    public string InstalledPath(ArtifactEntry entry) =>
        Path.Combine(Layout.ModelsRoot, entry.ArtifactId, entry.Revision, entry.FileName);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The only place these tests can get bytes from.
    ///
    /// <para>
    /// Every request is served from this dictionary, so a test that accidentally reached the real
    /// internet would fail rather than pass slowly. It honours range requests, because resuming a
    /// download is one of the behaviours under test and a handler that ignored the header would
    /// make the resumption test vacuous.
    /// </para>
    /// </summary>
    private sealed class OriginHandler(SetupFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();

            lock (fixture.Requests)
            {
                fixture.Requests[url] = fixture.Requests.GetValueOrDefault(url) + 1;
            }

            if (fixture.Blocked.Contains(url))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (!fixture._origin.TryGetValue(url, out byte[]? content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (fixture.Substituted.TryGetValue(url, out byte[]? substitute))
            {
                content = substitute;
            }

            long from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;

            if (from >= content.Length)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            byte[] body = from == 0 ? content : content[(int)from..];

            HttpResponseMessage response = new(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
            };

            if (from > 0)
            {
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            }

            return Task.FromResult(response);
        }
    }
}

/// <summary>A machine described rather than detected, so every branch can be reached.</summary>
public sealed class FakeHardwareProbe(HardwareSnapshot snapshot) : IHardwareProbe
{
    public int Probes { get; private set; }

    public Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        Probes++;
        return Task.FromResult(snapshot);
    }
}

/// <summary>Machines the recommendation tests describe.</summary>
public static class Machines
{
    public static HardwareSnapshot Base(
        long? memory = 32L * 1024 * 1024 * 1024,
        long? disk = 200L * 1024 * 1024 * 1024,
        bool? avx2 = true) => new()
    {
        OperatingSystem = "Windows 11",
        Architecture = "X64",
        CpuName = "Test CPU",
        LogicalCores = 8,
        HasAvx2 = avx2,
        TotalMemoryBytes = memory,
        AvailableDiskBytes = disk,
        DataVolume = "C:\\",
        InputDevices = [new AudioEndpointSummary("mic", "Microphone", true)],
        OutputDevices = [new AudioEndpointSummary("out", "Speakers", true)],
    };

    public static HardwareSnapshot WithNvidia(
        long? vram,
        CudaAvailability cuda = CudaAvailability.Available,
        long? memory = 32L * 1024 * 1024 * 1024,
        long? disk = 200L * 1024 * 1024 * 1024) => Base(memory, disk) with
    {
        Gpus =
        [
            new GpuInfo
            {
                Vendor = "NVIDIA",
                Model = "Test GPU",
                DedicatedMemoryBytes = vram,
                DriverVersion = "600.00",
            },
        ],
        Cuda = cuda,
    };

    public static HardwareSnapshot WithoutGpu(
        long? memory = 32L * 1024 * 1024 * 1024,
        long? disk = 200L * 1024 * 1024 * 1024) => Base(memory, disk) with
    {
        Gpus = [new GpuInfo { Vendor = "Intel", Model = "Integrated", DedicatedMemoryBytes = 128L * 1024 * 1024 }],
        Cuda = CudaAvailability.NoNvidiaAdapter,
    };
}
