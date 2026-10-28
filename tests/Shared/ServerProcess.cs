using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Bunting.Tests.Common;

/// <summary>Runs the real Bunting server in its own process on a Kestrel port, against an existing database.</summary>
public sealed class ServerProcess : IAsyncDisposable
{
    private readonly Process _process;

    private ServerProcess(Process process, string baseUrl)
    {
        _process = process;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static int FreeTcpPort() => FreePort();

    public static string RepoRoot { get; } = FindRepoRoot();

    public static async Task<ServerProcess> StartAsync(string connectionString, string adminToken, IReadOnlyDictionary<string, string>? extraEnvironment = null, int? port = null)
    {
        var baseUrl = $"http://127.0.0.1:{port ?? FreePort()}";
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var dll = Path.Combine(RepoRoot, "src", "Bunting.Server", "bin", configuration, "net10.0", "Bunting.Server.dll");

        var info = new ProcessStartInfo("dotnet", $"exec \"{dll}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["ASPNETCORE_URLS"] = baseUrl;
        info.Environment["ConnectionStrings__bunting"] = connectionString;
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        info.Environment["Bunting__Management__Tokens__0__Name"] = "process-owner";
        info.Environment["Bunting__Management__Tokens__0__Token"] = adminToken;
        info.Environment["Bunting__Management__Tokens__0__Role"] = "Owner";
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the server.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var server = new ServerProcess(process, baseUrl);
        await server.WaitUntilHealthy();
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    private async Task WaitUntilHealthy()
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException("The server exited during startup.");
            }

            try
            {
                if ((await http.GetAsync("/health")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("The server did not become healthy.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bunting.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Bunting.sln not found.");
    }
}
