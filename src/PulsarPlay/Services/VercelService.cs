using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarPlay.Services;

public class VercelConfig
{
    public string? ProjectId { get; set; }
    public string? OrgId { get; set; }
    public string? ProjectName { get; set; }
}

public class VercelService
{
    public event Action<string>? OutputReceived;
    public event Action<string>? ErrorReceived;

    private Process? _process;
    private CancellationTokenSource? _cts;

    public async Task<VercelConfig> ReadVercelConfig(string projectPath)
    {
        var config = new VercelConfig();

        try
        {
            var dotVercelPath = Path.Combine(projectPath, ".vercel", "project.json");
            if (File.Exists(dotVercelPath))
            {
                var json = await File.ReadAllTextAsync(dotVercelPath);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("projectId", out var projectId))
                    config.ProjectId = projectId.GetString();
                if (doc.RootElement.TryGetProperty("orgId", out var orgId))
                    config.OrgId = orgId.GetString();
                if (doc.RootElement.TryGetProperty("projectName", out var projectName))
                    config.ProjectName = projectName.GetString();
            }
            else
            {
                var vercelJsonPath = Path.Combine(projectPath, "vercel.json");
                if (File.Exists(vercelJsonPath))
                {
                    var json = await File.ReadAllTextAsync(vercelJsonPath);
                    var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("projectId", out var projectId))
                        config.ProjectId = projectId.GetString();
                    if (doc.RootElement.TryGetProperty("orgId", out var orgId))
                        config.OrgId = orgId.GetString();
                    if (doc.RootElement.TryGetProperty("projectName", out var projectName))
                        config.ProjectName = projectName.GetString();
                }
                else
                {
                    var nowJsonPath = Path.Combine(projectPath, "now.json");
                    if (File.Exists(nowJsonPath))
                    {
                        var json = await File.ReadAllTextAsync(nowJsonPath);
                        var doc = JsonDocument.Parse(json);

                        if (doc.RootElement.TryGetProperty("projectId", out var projectId))
                            config.ProjectId = projectId.GetString();
                        if (doc.RootElement.TryGetProperty("orgId", out var orgId))
                            config.OrgId = orgId.GetString();
                        if (doc.RootElement.TryGetProperty("name", out var name))
                            config.ProjectName = name.GetString();
                    }
                }
            }
        }
        catch
        {
        }

        return config;
    }

    public async Task FetchLogsAsync(string projectPath, string? orgId = null, string? projectName = null, int limit = 50)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            string vercelPath = FindVercelCli();
            if (string.IsNullOrEmpty(vercelPath))
            {
                ErrorReceived?.Invoke("Vercel CLI not found. Please install: npm i -g vercel");
                return;
            }

            var args = $"logs --project {projectName ?? projectPath} --limit {limit}";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {vercelPath} {args}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projectPath
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Start();

            _ = ReadOutputAsync(_process.StandardOutput, OutputReceived, token);
            _ = ReadOutputAsync(_process.StandardError, ErrorReceived, token);

            await Task.Run(() => _process.WaitForExit(), token);
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke($"Error fetching Vercel logs: {ex.Message}");
        }
    }

    public async Task StreamLogsAsync(string projectPath, string? orgId = null, string? projectName = null)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            string vercelPath = FindVercelCli();
            if (string.IsNullOrEmpty(vercelPath))
            {
                ErrorReceived?.Invoke("Vercel CLI not found. Please install: npm i -g vercel");
                return;
            }

            var args = $"logs --project {projectName ?? projectPath} --follow";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {vercelPath} {args}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projectPath
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Start();

            _ = ReadOutputAsync(_process.StandardOutput, OutputReceived, token);
            _ = ReadOutputAsync(_process.StandardError, ErrorReceived, token);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke($"Error streaming Vercel logs: {ex.Message}");
        }
    }

    private string? FindVercelCli()
    {
        var npmPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm"),
            @"C:\Program Files\nodejs",
            @"C:\Program Files (x86)\nodejs"
        };

        var possiblePaths = new[] { "vercel.cmd", "vercel.exe", "vercel" };

        foreach (var npmPath in npmPaths)
        {
            if (Directory.Exists(npmPath))
            {
                foreach (var vercelFile in possiblePaths)
                {
                    var fullPath = Path.Combine(npmPath, vercelFile);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
        }

        return "vercel";
    }

    private async Task ReadOutputAsync(StreamReader reader, Action<string>? handler, CancellationToken token)
    {
        var buffer = new char[1024];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                var text = new string(buffer, 0, bytesRead);
                handler?.Invoke(text);
            }
        }
        catch { }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch { }

        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
    }
}
