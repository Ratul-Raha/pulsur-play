using System;
using System.Drawing;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms;
using PulsarPlay.Services;

namespace PulsarPlay;

public class PortInfo
{
    public string Port { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Process { get; set; } = "";
    public bool IsActive { get; set; }
    public string Command { get; set; } = "";
    public string Folder { get; set; } = "";
}

public class ProjectInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Port { get; set; } = "";
    public string Command { get; set; } = "";
    public bool IsActive { get; set; }
    public string Pid { get; set; } = "";
    public bool IsParent { get; set; }
    public string Branch { get; set; } = "";
    public List<ProjectInfo> Children { get; set; } = new();
}

public class CustomCommand
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
}

public class ProcessInfo
{
    public string Name { get; set; } = "";
    public double Percent { get; set; }
}

public partial class MainWindow : Window
{
    private bool _isDragging;
    private System.Windows.Point _dragStart;
    private DispatcherTimer _updateTimer = null!;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private PerformanceCounter? _netSentCounter;
    private PerformanceCounter? _netReceivedCounter;
    private PerformanceCounter? _netSentCounter2;
    private PerformanceCounter? _netReceivedCounter2;
    private DateTime _startTime;
    private NotifyIcon? _trayIcon;
    private readonly Dictionary<string, DateTime> _alertHistory = new();

    private int _ramWarning = 80;
    private int _ramCritical = 90;
    private int _cpuWarning = 80;
    private int _cpuCritical = 95;
    private readonly List<string> _notifications = new();
    private int _notificationCount;
    private DispatcherTimer _smartAlertTimer = null!;
    private readonly List<PortInfo> _portInfos = new();
    private readonly List<CustomCommand> _customCommands = new();
    private readonly List<ProjectInfo> _projects = new();
    private readonly List<PortInfo> _activePorts = new();
    private string _rootFolder = "";
    private readonly SystemMonitorService _systemMonitorService = new();
    private readonly ProjectService _projectService = new();
    private readonly DataService _dataService = new();
    private string _currentBrowserUrl = "";

    private void StartProject_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path == null) return;

        var proj = _projects.FirstOrDefault(p => p.Path == path);
        if (proj == null) return;

        if (!string.IsNullOrEmpty(proj.Command))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd", "/c cd /d \"" + proj.Path + "\" && " + proj.Command)
                {
                    UseShellExecute = true,
                    WorkingDirectory = proj.Path
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }
    }

    private int FindAvailablePort(int startPort)
    {
        for (int port = startPort; port < startPort + 100; port++)
        {
            if (!IsPortInUse(port))
                return port;
        }
        return 0;
    }

    private bool IsPortInUse(int port)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Contains(":" + port);
        }
        catch { return false; }
    }

    private void StopProject_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path == null) return;

        var proj = _projects.FirstOrDefault(p => p.Path == path);
        if (proj == null) return;

        if (!string.IsNullOrEmpty(proj.Pid) && int.TryParse(proj.Pid, out int pid))
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(pid).Kill();
            }
            catch { }
        }
    }

    private void Folder_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            var proj = (sender as System.Windows.Controls.StackPanel)?.DataContext as ProjectInfo 
                ?? (sender as System.Windows.Controls.TextBlock)?.DataContext as ProjectInfo;
            
            if (proj != null && Directory.Exists(proj.Path))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", proj.Path);
                }
                catch { }
            }
        }
    }

    private void LoadProjectSettingsForNav()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "projects.json");
            if (File.Exists(path))
            {
                var lines = File.ReadAllText(path).Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("|"))
                    {
                        var parts = line.Split(new[] { '|' }, 3);
                        if (parts.Length >= 3)
                        {
                            var projPath = parts[0].Trim();
                            var proj = _projects.FirstOrDefault(p => p.Path == projPath);
                            if (proj != null)
                            {
                                proj.Port = parts[1].Trim();
                                proj.Command = parts[2].Trim();
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void RefreshPorts()
    {
        _activePorts.Clear();
        var currentPorts = new Dictionary<string, string>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            var proc = System.Diagnostics.Process.Start(psi);
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var lines = output.Split('\n');

            foreach (var line in lines)
            {
                if (line.Contains("LISTENING") && line.Contains("127.0.0.1"))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5)
                    {
                        var addr = parts[1];
                        if (addr.Contains(':'))
                        {
                            var port = addr.Split(':').Last();
                            if (int.TryParse(port, out int portNum) && portNum > 0 && !currentPorts.ContainsKey(port))
                            {
                                var pid = parts[4];
                                currentPorts[port] = pid;
                                _activePorts.Add(new PortInfo { Port = port, Pid = pid, Process = GetProcessName(pid), IsActive = true });
                            }
                        }
                    }
                }
            }
        }
        catch { }
        
        if (ActivePortsList != null)
            ActivePortsList.ItemsSource = _activePorts.ToList();
    }

    private string GetProcessName(string pid)
    {
        try
        {
            if (int.TryParse(pid, out int pidNum))
            {
                var proc = System.Diagnostics.Process.GetProcessById(pidNum);
                return proc.ProcessName;
            }
        }
        catch { }
        return "Unknown";
    }

    private void KillPort_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var port = btn?.Tag?.ToString();
        if (port != null)
        {
            var portInfo = _portInfos.FirstOrDefault(p => p.Port == port);
            if (portInfo != null && portInfo.IsActive && int.TryParse(portInfo.Pid, out int pid))
            {
                try
                {
                    System.Diagnostics.Process.GetProcessById(pid).Kill();
                }
                catch { }
            }
            RefreshPorts();
        }
    }

    private void RestartPort_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var port = btn?.Tag?.ToString();
        if (port != null)
        {
            var portInfo = _portInfos.FirstOrDefault(p => p.Port == port);
            if (portInfo != null && portInfo.IsActive && int.TryParse(portInfo.Pid, out int pid))
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    var filename = proc.MainModule?.FileName;
                    var args = proc.StartInfo.Arguments;
                    proc.Kill();
                    if (!string.IsNullOrEmpty(filename))
                    {
                        System.Threading.Thread.Sleep(1000);
                        System.Diagnostics.Process.Start(filename, args);
                    }
                }
                catch { }
            }
            RefreshPorts();
        }
    }

    private void StartPort_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var port = btn?.Tag?.ToString();
        if (port == null) return;

        var existing = _portInfos.FirstOrDefault(p => p.Port == port);
        if (existing != null && !existing.IsActive && !string.IsNullOrEmpty(existing.Command))
        {
            try
            {
                var cmd = existing.Command;
                if (!string.IsNullOrEmpty(existing.Folder) && Directory.Exists(existing.Folder))
                {
                    cmd = "cd /d \"" + existing.Folder + "\" && " + cmd;
                }
                var psi = new System.Diagnostics.ProcessStartInfo("cmd", "/c " + cmd)
                {
                    UseShellExecute = true,
                    WorkingDirectory = !string.IsNullOrEmpty(existing.Folder) && Directory.Exists(existing.Folder) ? existing.Folder : null
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
            RefreshPorts();
        }
    }

    private void AddPort_Click(object sender, RoutedEventArgs e)
    {
        var selectFolder = new System.Windows.Forms.FolderBrowserDialog();
        selectFolder.Description = "Select Project Folder";
        
        if (selectFolder.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var selectedPath = selectFolder.SelectedPath;
            var name = Path.GetFileName(selectedPath);
            
            var existing = _projects.FirstOrDefault(p => p.Path == selectedPath);
            if (existing != null)
            {
                System.Windows.MessageBox.Show("This folder is already in the list.", "Already Added", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            
            var newProject = new ProjectInfo
            {
                Name = name,
                Path = selectedPath,
                Port = "",
                Command = ""
            };
            
            _projects.Add(newProject);
            SaveProjects();
            PortList.ItemsSource = _projects.OrderBy(p => p.Name).ToList();
        }
    }

    private void StartAllProjects_Click(object sender, RoutedEventArgs e)
    {
        foreach (var proj in _projects)
        {
            if (!string.IsNullOrEmpty(proj.Command))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd", "/c cd /d \"" + proj.Path + "\" && " + proj.Command)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = proj.Path
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch { }
            }
        }
    }

    private ProjectInfo FindProjectByPath(string path)
    {
        return _projects.FirstOrDefault(p => p.Path == path);
    }

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        var proj = FindProjectByPath(path);
        
        if (proj == null) return;

        var editWindow = new Window
        {
            Title = "Edit: " + proj.Name,
            Width = 450,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Edit: " + proj.Name, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 0, 0, 20) });

        var pathLabel = new TextBlock { Text = "Path:", Foreground = System.Windows.Media.Brushes.LightGray };
        panel.Children.Add(pathLabel);
        panel.Children.Add(new TextBlock { Text = proj.Path, FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) });

        var portLabel = new TextBlock { Text = "Port (e.g., 3000):", Foreground = System.Windows.Media.Brushes.LightGray };
        panel.Children.Add(portLabel);
        var portBox = new System.Windows.Controls.TextBox { Text = proj.Port, FontSize = 14, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Width = 100, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 15) };
        panel.Children.Add(portBox);

        var cmdLabel = new TextBlock { Text = "Start Command (e.g., npm run dev):", Foreground = System.Windows.Media.Brushes.LightGray };
        panel.Children.Add(cmdLabel);
        var cmdBox = new System.Windows.Controls.TextBox { Text = proj.Command, FontSize = 14, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 15) };
        panel.Children.Add(cmdBox);

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var saveBtn = new System.Windows.Controls.Button { Content = "Save", Width = 80, Height = 32, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Foreground = System.Windows.Media.Brushes.Black, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Height = 32, Background = System.Windows.Media.Brushes.Gray, Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(10, 0, 0, 0) };

        saveBtn.Click += (s, args) =>
        {
            proj.Port = portBox.Text;
            proj.Command = cmdBox.Text;
            SaveProjects();
            editWindow.Close();
        };
        cancelBtn.Click += (s, args) => editWindow.Close();
        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        editWindow.Content = panel;
        editWindow.ShowDialog();
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path == null) return;

        var proj = _projects.FirstOrDefault(p => p.Path == path);
        if (proj == null) return;

        var confirmWindow = new Window
        {
            Title = "Remove Project",
            Width = 350,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };

        var panel = new StackPanel { Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Remove " + proj.Name + "?", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 76, 76)), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "This will remove the project from the list.", FontSize = 12, Foreground = System.Windows.Media.Brushes.Gray, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) });

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        var removeBtn = new System.Windows.Controls.Button { Content = "Remove", Width = 80, Height = 32, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 76, 76)), Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Height = 32, Background = System.Windows.Media.Brushes.Gray, Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(10, 0, 0, 0) };

        removeBtn.Click += (s, args) =>
        {
            _projects.Remove(proj);
            SaveProjects();
            PortList.ItemsSource = _projects.OrderBy(p => p.Name).ToList();
            confirmWindow.Close();
        };
        cancelBtn.Click += (s, args) => confirmWindow.Close();
        btnPanel.Children.Add(removeBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        confirmWindow.Content = panel;
        confirmWindow.ShowDialog();
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var confirmWindow = new Window
        {
            Title = "Delete All Projects",
            Width = 350,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };

        var panel = new StackPanel { Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Delete ALL projects?", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 76, 76)), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "All saved projects will be removed from list.", FontSize = 12, Foreground = System.Windows.Media.Brushes.Gray, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) });

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        var deleteBtn = new System.Windows.Controls.Button { Content = "Delete All", Width = 80, Height = 32, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 76, 76)), Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Height = 32, Background = System.Windows.Media.Brushes.Gray, Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(10, 0, 0, 0) };

        deleteBtn.Click += (s, args) =>
        {
            _projects.Clear();
            SaveProjects();
            confirmWindow.Close();
        };
        cancelBtn.Click += (s, args) => confirmWindow.Close();
        btnPanel.Children.Add(deleteBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        confirmWindow.Content = panel;
        confirmWindow.ShowDialog();
    }

    private void ScanSubfolders(string folder)
    {
        if (!Directory.Exists(folder)) return;
        
        foreach (var dir in Directory.GetDirectories(folder))
        {
            var name = Path.GetFileName(dir);
            if (!_projects.Any(p => p.Path == dir))
            {
                _projects.Add(new ProjectInfo { Name = name, Path = dir });
            }
        }
        
        LoadProjectSettings();
        PortList.ItemsSource = _projects.OrderBy(p => p.Name).ToList();
    }

    private void ScanProjects()
    {
        _projects.Clear();
        if (Directory.Exists(_rootFolder))
        {
            foreach (var dir in Directory.GetDirectories(_rootFolder))
            {
                var name = Path.GetFileName(dir);
                var existing = _projects.FirstOrDefault(p => p.Path == dir);
                if (existing != null)
                {
                    _projects.Add(existing);
                }
                else
                {
                    _projects.Add(new ProjectInfo { Name = name, Path = dir });
                }
            }
        }
        LoadProjectSettings();
    }

    private void LoadProjectSettings()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "projects.json");
            if (File.Exists(path))
            {
                var lines = File.ReadAllText(path).Split('\n');
                var loadedPaths = new HashSet<string>();
                
                foreach (var line in lines)
                {
                    if (line.Contains("|"))
                    {
                        var parts = line.Split(new[] { '|' }, 4);
                        if (parts.Length >= 1)
                        {
                            var projPath = parts[0].Trim();
                            if (string.IsNullOrEmpty(projPath)) continue;
                            
                            if (Directory.Exists(projPath) && !loadedPaths.Contains(projPath))
                            {
                                loadedPaths.Add(projPath);
                                var name = Path.GetFileName(projPath);
                                var existing = _projects.FirstOrDefault(p => p.Path == projPath);
                                if (existing != null)
                                {
                                    if (parts.Length >= 2) existing.Port = parts[1].Trim();
                                    if (parts.Length >= 3) existing.Command = parts[2].Trim();
                                }
                                else
                                {
                                    _projects.Add(new ProjectInfo 
                                    { 
                                        Name = name, 
                                        Path = projPath,
                                        Port = parts.Length >= 2 ? parts[1].Trim() : "",
                                        Command = parts.Length >= 3 ? parts[2].Trim() : ""
                                    });
                                }
                            }
                        }
                    }
                }
                
                PortList.ItemsSource = _projects.OrderBy(p => p.Name).ToList();

                foreach (var proj in _projects)
                {
                    proj.Branch = GetGitBranch(proj.Path);
                }

                if (PortList != null)
                    PortList.ItemsSource = _projects.OrderBy(p => p.Name).ToList();
                if (PortListMax != null)
                    PortListMax.ItemsSource = _projects.OrderBy(p => p.Name).ToList();
            }
        }
        catch { }
    }

    private string GetGitBranch(string projPath)
    {
        try
        {
            var gitHeadPath = Path.Combine(projPath, ".git", "HEAD");
            if (File.Exists(gitHeadPath))
            {
                var headContent = File.ReadAllText(gitHeadPath).Trim();
                if (headContent.StartsWith("ref: refs/heads/"))
                    return headContent.Replace("ref: refs/heads/", "").Trim();
                else if (headContent.StartsWith("ref: "))
                    return headContent.Replace("ref: ", "").Trim();
            }
        }
        catch { }
        return "main";
    }

    private void SaveProjects()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "projects.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            
            var lines = new List<string>();
            
            foreach (var proj in _projects)
            {
                lines.Add(proj.Path + "|" + proj.Port + "|" + proj.Command);
            }
            
            File.WriteAllText(path, string.Join("\n", lines));
        }
        catch { }
    }

    private void SavePorts()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "ports.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = _portInfos.Where(p => !string.IsNullOrEmpty(p.Command))
                .Select(p => p.Port + "|" + p.Folder + "|" + p.Command);
            File.WriteAllText(path, string.Join("\n", lines));
        }
        catch { }
    }

    private void LoadPorts()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "ports.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var lines = json.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("|"))
                    {
                        var parts = line.Split(new[] { '|' }, 3);
                        if (parts.Length >= 3 && int.TryParse(parts[0].Trim(), out int portNum))
                        {
                            if (!_portInfos.Any(p => p.Port == parts[0].Trim()))
                            {
                                _portInfos.Add(new PortInfo { Port = parts[0].Trim(), Process = "Available", IsActive = false, Folder = parts[1].Trim(), Command = parts[2].Trim() });
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void LoadCommands()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "commands.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var lines = json.Split('\n');
                _customCommands.Clear();
                foreach (var line in lines)
                {
                    if (line.Contains(":"))
                    {
                        var parts = line.Split(new[] { ':' }, 2);
                        if (parts.Length == 2)
                            _customCommands.Add(new CustomCommand { Name = parts[0].Trim(), Command = parts[1].Trim() });
                    }
                }
            }
        }
        catch { }
        if (CommandList != null)
            CommandList.ItemsSource = _customCommands.ToList();
    }

    private void SaveCommands()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "commands.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = _customCommands.Select(c => c.Name + ":" + c.Command);
            File.WriteAllText(path, string.Join("\n", lines));
        }
        catch { }
    }

    private void DelCommand_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var name = btn?.Tag?.ToString();
        if (name != null)
        {
            var cmd = _customCommands.FirstOrDefault(c => c.Name == name);
            if (cmd != null)
            {
                _customCommands.Remove(cmd);
                SaveCommands();
                LoadCommands();
            }
        }
    }

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "Add Command", Width = 350, Height = 180, WindowStyle = WindowStyle.ToolWindow, Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(10) };
        var nameBox = new System.Windows.Controls.TextBox { Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5) };
        var cmdBox = new System.Windows.Controls.TextBox { Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5) };
        var nameLbl = new TextBlock { Text = "Name:", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 3) };
        var cmdLbl = new TextBlock { Text = "Command:", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 3) };
        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var saveBtn = new System.Windows.Controls.Button { Content = "Save", Width = 60, Background = System.Windows.Media.Brushes.Gray, Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 60 };
        panel.Children.Add(nameLbl);
        panel.Children.Add(nameBox);
        panel.Children.Add(cmdLbl);
        panel.Children.Add(cmdBox);
        saveBtn.Click += (s, args) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text) && !string.IsNullOrWhiteSpace(cmdBox.Text))
            {
                _customCommands.Add(new CustomCommand { Name = nameBox.Text, Command = cmdBox.Text });
                SaveCommands();
                CommandList.ItemsSource = _customCommands.ToList();
                dialog.Close();
            }
        };
        cancelBtn.Click += (s, args) => dialog.Close();
        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var cmd = btn?.Tag?.ToString();
        if (!string.IsNullOrEmpty(cmd))
        {
            try
            {
                var parts = cmd.Split(' ', 2);
                if (parts.Length == 1)
                    System.Diagnostics.Process.Start("cmd", "/c " + cmd);
                else
                    System.Diagnostics.Process.Start(parts[0], parts[1]);
            }
            catch { }
        }
    }

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var name = btn?.Tag?.ToString();
        if (!string.IsNullOrEmpty(name))
        {
            var cmd = _customCommands.FirstOrDefault(c => c.Name == name);
            if (cmd != null)
            {
                _customCommands.Remove(cmd);
                SaveCommands();
                LoadCommands();
            }
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStart = e.GetPosition(this);
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isDragging)
        {
            System.Windows.Point pos = e.GetPosition(this);
            Left += pos.X - _dragStart.X;
            Top += pos.Y - _dragStart.Y;
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _trayIcon?.ShowBalloonTip(2000, "DevHealth", "Minimized to tray", ToolTipIcon.Info);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            Width = 450;
            Height = 620;
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void ShowPreviewWindow()
    {
        var preview = new Window
        {
            Title = "DevHealth Preview",
            Width = 800,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };

        var scroll = new System.Windows.Controls.ScrollViewer { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
        var stack = new StackPanel { Margin = new Thickness(20) };

        stack.Children.Add(new TextBlock { Text = "📊 System Overview", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 0, 0, 15) });

        var cpuVal = _cpuCounter?.NextValue() ?? 0;
        var ramVal = _ramCounter?.NextValue() ?? 0;
        var diskVal = (_diskReadCounter?.NextValue() ?? 0) / 1024 / 1024;
        var netVal = (_netSentCounter?.NextValue() ?? 0) / 1024 / 1024;

        stack.Children.Add(CreateStatBlock("CPU Usage", cpuVal.ToString("F1") + "%", "#CE9178"));
        stack.Children.Add(CreateStatBlock("RAM Usage", ramVal.ToString("F1") + "%", "#4EC9B0"));
        stack.Children.Add(CreateStatBlock("Disk I/O", diskVal.ToString("F1") + " MB/s", "#569CD6"));
        stack.Children.Add(CreateStatBlock("Network", netVal.ToString("F1") + " MB/s", "#DCDCAA"));

        stack.Children.Add(new TextBlock { Text = "📁 Projects (" + _projects.Count + ")", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 20, 0, 10) });

        foreach (var proj in _projects)
        {
            var status = _activePorts.Any(p => p.Port == proj.Port) ? "🟢 Running" : "⚪ Stopped";
            stack.Children.Add(new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38)), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 5), CornerRadius = new System.Windows.CornerRadius(4) });
        }

        stack.Children.Add(new TextBlock { Text = "🔌 Active Ports (" + _activePorts.Count + ")", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 20, 0, 10) });

        foreach (var port in _activePorts)
        {
            stack.Children.Add(new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38)), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 5), CornerRadius = new System.Windows.CornerRadius(4), Child = new TextBlock { Text = "Port: " + port.Port + " - " + port.Process, Foreground = System.Windows.Media.Brushes.White } });
        }

        scroll.Content = stack;
        preview.Content = scroll;
        preview.ShowDialog();
    }

    private Border CreateStatBlock(string label, string value, string color)
    {
        var colorBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        return new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38)), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new System.Windows.CornerRadius(6), Child = new StackPanel { Children = { new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = colorBrush }, new TextBlock { Text = label, FontSize = 12, Foreground = System.Windows.Media.Brushes.Gray } } } };
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new Window
        {
            Title = "DevHealth Settings",
            Width = 420,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

        var scrollPanel = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20) };
        var panel = new StackPanel();

        var title = new TextBlock { Text = "⚙️ Settings", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(title);

        var alertsSection = CreateSectionHeader("🔔 Alert Thresholds");
        panel.Children.Add(alertsSection);

        var ramGrid = CreateInputRow("💾 RAM Warning", _ramWarning.ToString(), "1-99");
        var ramWarnBox = ramGrid.Children[1] as System.Windows.Controls.TextBox;
        panel.Children.Add(ramGrid);

        var ramCritGrid = CreateInputRow("💾 RAM Critical", _ramCritical.ToString(), "1-100");
        var ramCritBox = ramCritGrid.Children[1] as System.Windows.Controls.TextBox;
        panel.Children.Add(ramCritGrid);

        var cpuWarnGrid = CreateInputRow("🔥 CPU Warning", _cpuWarning.ToString(), "1-99");
        var cpuWarnBox = cpuWarnGrid.Children[1] as System.Windows.Controls.TextBox;
        panel.Children.Add(cpuWarnGrid);

        var cpuCritGrid = CreateInputRow("🔥 CPU Critical", _cpuCritical.ToString(), "1-100");
        var cpuCritBox = cpuCritGrid.Children[1] as System.Windows.Controls.TextBox;
        panel.Children.Add(cpuCritGrid);

        var performanceSection = CreateSectionHeader("⚡ Performance");
        panel.Children.Add(performanceSection);

        var refreshGrid = CreateInputRow("⏱️ Refresh Rate (sec)", "2", "1-60");
        var refreshBox = refreshGrid.Children[1] as System.Windows.Controls.TextBox;
        panel.Children.Add(refreshGrid);

        var startupSection = CreateSectionHeader("🚀 Startup");
        panel.Children.Add(startupSection);

        var startupCheck = new System.Windows.Controls.CheckBox { Content = "Start with Windows", Foreground = System.Windows.Media.Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 14, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(startupCheck);

        var versionInfo = new TextBlock { Text = "DevHealth Monitor v1.0.0", Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        panel.Children.Add(versionInfo);

        scrollPanel.Content = panel;
        mainGrid.Children.Add(scrollPanel);

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        var saveBtn = new System.Windows.Controls.Button { Content = "💾 Save", Width = 100, Height = 32, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Foreground = System.Windows.Media.Brushes.Black, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Height = 32, Background = System.Windows.Media.Brushes.Gray, Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0) };

        saveBtn.Click += (s, args) =>
        {
            if (int.TryParse(ramWarnBox.Text, out int rw) && rw > 0 && rw < 100) _ramWarning = rw;
            if (int.TryParse(ramCritBox.Text, out int rc) && rc > 0 && rc <= 100) _ramCritical = rc;
            if (int.TryParse(cpuWarnBox.Text, out int cw) && cw > 0 && cw < 100) _cpuWarning = cw;
            if (int.TryParse(cpuCritBox.Text, out int cc) && cc > 0 && cc <= 100) _cpuCritical = cc;
            if (int.TryParse(refreshBox.Text, out int rr) && rr >= 1 && rr <= 60) _updateTimer.Interval = TimeSpan.FromSeconds(rr);

            if (startupCheck.IsChecked == true)
            {
                try { Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true).SetValue("PulsarPlay", System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName); }
                catch { }
            }
            else
            {
                try { Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true).DeleteValue("PulsarPlay", false); }
                catch { }
            }

            SaveSettings();
            settingsWindow.Close();
        };

        cancelBtn.Click += (s, args) => settingsWindow.Close();

        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);

        var btnBorder = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38)), Child = btnPanel };
        Grid.SetRow(btnBorder, 1);
        mainGrid.Children.Add(btnBorder);

        settingsWindow.Content = mainGrid;
        settingsWindow.ShowDialog();
    }

    private TextBlock CreateSectionHeader(string text)
    {
        return new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 170)), Margin = new Thickness(0, 15, 0, 10) };
    }

    private Grid CreateInputRow(string label, string defaultValue, string hint)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.LightGray, FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var textBox = new System.Windows.Controls.TextBox { Text = defaultValue, FontSize = 14, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)), Foreground = System.Windows.Media.Brushes.White, BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)), Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(textBox);

        var hintBlock = new TextBlock { Text = hint, Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(hintBlock, 2);
        grid.Children.Add(hintBlock);

        return grid;
    }

    private void SaveSettings()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, $"{{\"ramWarning\":{_ramWarning},\"ramCritical\":{_ramCritical},\"cpuWarning\":{_cpuWarning},\"cpuCritical\":{_cpuCritical}}}");
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulsarPlay", "settings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var parts = json.Replace("{", "").Replace("}", "").Split(',');
                foreach (var part in parts)
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2)
                    {
                        if (kv[0].Contains("ramWarning") && int.TryParse(kv[1], out int ramW)) _ramWarning = ramW;
                        else if (kv[0].Contains("ramCritical") && int.TryParse(kv[1], out int ramC)) _ramCritical = ramC;
                        else if (kv[0].Contains("cpuWarning") && int.TryParse(kv[1], out int cpuW)) _cpuWarning = cpuW;
                        else if (kv[0].Contains("cpuCritical") && int.TryParse(kv[1], out int cpuC)) _cpuCritical = cpuC;
                    }
                }
            }
        }
        catch { }
    }

    private void CleanCache_Click(object sender, RoutedEventArgs e)
    {
        var cleanWindow = new Window
        {
            Title = "🧹 System Cache Cleaner",
            Width = 480,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });

        var scrollPanel = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20) };
        var panel = new StackPanel();

        var title = new TextBlock { Text = "🧹 Cache Cleaner", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 0, 0, 10) };
        var subtitle = new TextBlock { Text = "Select items to clean:", Foreground = System.Windows.Media.Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 0, 0, 15) };
        panel.Children.Add(title);
        panel.Children.Add(subtitle);

        var tempUser = GetFolderSize(Path.GetTempPath());
        var tempCheck = CreateCacheRow("📁 User Temp Files", tempUser, "%TEMP%", true);
        panel.Children.Add(tempCheck);

        var thumbCache = GetFolderSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer"));
        var thumbCheck = CreateCacheRow("🖼️ Thumbnail Cache", thumbCache, "Explorer", true);
        panel.Children.Add(thumbCheck);

        var recent = GetFolderSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent"));
        var recentCheck = CreateCacheRow("📋 Recent Files", recent, "Shortcuts", true);
        panel.Children.Add(recentCheck);

        var prefetchPath = @"C:\Windows\Prefetch";
        long prefetchSize = 0;
        try { prefetchSize = GetFolderSize(prefetchPath); } catch { }
        var prefetchCheck = CreateCacheRow("⚡ Prefetch Data", prefetchSize, "Windows Prefetch", true);
        panel.Children.Add(prefetchCheck);

        var totalSize = tempUser + thumbCache + recent + prefetchSize;
        var totalBorder = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)), CornerRadius = new System.Windows.CornerRadius(8), Padding = new Thickness(15), Margin = new Thickness(0, 15, 0, 0) };
        var totalGrid = new Grid();
        totalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        totalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        totalGrid.Children.Add(new TextBlock { Text = "Total Space to Free:", Foreground = System.Windows.Media.Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var totalSizeText = new TextBlock { Text = FormatBytes(totalSize), Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 170)), FontSize = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        Grid.SetColumn(totalSizeText, 1);
        totalGrid.Children.Add(totalSizeText);
        totalBorder.Child = totalGrid;
        panel.Children.Add(totalBorder);

        var infoText = new TextBlock { Text = "💡 Tip: Clearing temp files can free up significant space and improve performance.", Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 0) };
        panel.Children.Add(infoText);

        scrollPanel.Content = panel;
        mainGrid.Children.Add(scrollPanel);

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var scanBtn = new System.Windows.Controls.Button { Content = "🔄 Rescan", Width = 100, Height = 36, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)), Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 15, 0), BorderThickness = new Thickness(0) };
        var cleanBtn = new System.Windows.Controls.Button { Content = "🗑️ Clean Now", Width = 120, Height = 36, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 76, 76)), Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
        var closeBtn = new System.Windows.Controls.Button { Content = "Close", Width = 80, Height = 36, Background = System.Windows.Media.Brushes.Gray, Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0) };

        scanBtn.Click += (s, args) =>
        {
            tempUser = GetFolderSize(Path.GetTempPath());
            thumbCache = GetFolderSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer"));
            recent = GetFolderSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent"));
            prefetchSize = 0;
            try { prefetchSize = GetFolderSize(prefetchPath); } catch { }
            totalSize = tempUser + thumbCache + recent + prefetchSize;

            panel.Children.Clear();
            panel.Children.Add(title);
            panel.Children.Add(subtitle);
            panel.Children.Add(CreateCacheRow("📁 User Temp Files", tempUser, "%TEMP%", true));
            panel.Children.Add(CreateCacheRow("🖼️ Thumbnail Cache", thumbCache, "Explorer", true));
            panel.Children.Add(CreateCacheRow("📋 Recent Files", recent, "Shortcuts", true));
            panel.Children.Add(CreateCacheRow("⚡ Prefetch Data", prefetchSize, "Windows Prefetch", true));

            totalBorder.Child = totalGrid;
            panel.Children.Add(totalBorder);
totalSizeText.Text = FormatBytes(totalSize);
            panel.Children.Add(infoText);
        };

        cleanBtn.Click += (s, args) =>
        {
            var result = System.Windows.MessageBox.Show($"This will delete {FormatBytes(totalSize)} of cache data.\n\nDo you want to continue?", "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                long cleaned = 0;
                cleaned += CleanFolder(Path.GetTempPath());
                cleaned += CleanFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer"), "thumbcache_*.db");
                cleaned += CleanFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent"));
                try { CleanFolder(prefetchPath); } catch { }

                System.Windows.MessageBox.Show($"✅ Successfully cleaned {FormatBytes(cleaned)}!", "Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                cleanWindow.Close();
            }
        };

        closeBtn.Click += (s, args) => cleanWindow.Close();

        btnPanel.Children.Add(scanBtn);
        btnPanel.Children.Add(cleanBtn);
        btnPanel.Children.Add(closeBtn);

        var btnBorder = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38)), Child = btnPanel };
        Grid.SetRow(btnBorder, 1);
        mainGrid.Children.Add(btnBorder);

        cleanWindow.Content = mainGrid;
        cleanWindow.ShowDialog();
    }

    private Border CreateCacheRow(string name, long size, string path, bool isChecked)
    {
        var border = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)), CornerRadius = new System.Windows.CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

        var checkBox = new System.Windows.Controls.CheckBox { IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(checkBox, 0);
        grid.Children.Add(checkBox);

        var textPanel = new StackPanel();
        textPanel.Children.Add(new TextBlock { Text = name, Foreground = System.Windows.Media.Brushes.White, FontSize = 13, FontWeight = FontWeights.Medium });
        textPanel.Children.Add(new TextBlock { Text = path, Foreground = System.Windows.Media.Brushes.Gray, FontSize = 10 });
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        var sizeBlock = new TextBlock { Text = FormatBytes(size), Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(206, 145, 120)), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sizeBlock, 2);
        grid.Children.Add(sizeBlock);

        border.Child = grid;
        return border;
    }

    private long GetFolderSize(string path)
    {
        long size = 0;
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
            }
        }
        catch { }
        return size;
    }

    private long CleanFolder(string path, string pattern = "*")
    {
        long cleaned = 0;
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        cleaned += new FileInfo(file).Length;
                        File.Delete(file);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return cleaned;
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return $"{size:F2} {sizes[order]}";
    }

    private Window? _browserWindow;

    private void ViewProject_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (string.IsNullOrEmpty(path)) return;

        var proj = _projects.FirstOrDefault(p => p.Path == path);
        if (proj == null) return;

        if (string.IsNullOrEmpty(proj.Port))
        {
            System.Windows.MessageBox.Show("Please set a port in Edit first.", "No Port", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var url = "http://localhost:" + proj.Port;

        if (WindowState == WindowState.Maximized)
        {
            OpenInMaxWebView(url);
        }
        else
        {
            OpenBrowserWindow(url);
        }
    }

    private async void OpenInMaxWebView(string url)
    {
        try
        {
            if (MaxWebView == null) return;
            MaxUrlBox.Text = url;
            await MaxWebView.EnsureCoreWebView2Async();
            MaxWebView.CoreWebView2.Navigate(url);
            if (TimelineStatus != null)
                TimelineStatus.Text = " - " + url;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Error: " + ex.Message);
        }
    }

    private async void OpenBrowserWindow(string url)
    {
        try
        {
            if (_browserWindow != null)
            {
                _browserWindow.Close();
            }

            _browserWindow = new Window
            {
                Title = "Browser - " + url,
                Width = 900,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(40) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            var toolbar = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)),
                Height = 40
            };

            var backBtn = new System.Windows.Controls.Button
            {
                Content = "◀",
                Width = 36,
                Height = 28,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new System.Windows.Thickness(0),
                Margin = new System.Windows.Thickness(5, 0, 2, 0),
                ToolTip = "Back"
            };

            var forwardBtn = new System.Windows.Controls.Button
            {
                Content = "▶",
                Width = 36,
                Height = 28,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new System.Windows.Thickness(0),
                Margin = new System.Windows.Thickness(2, 0, 2, 0),
                ToolTip = "Forward"
            };

            var refreshBtn = new System.Windows.Controls.Button
            {
                Content = "⟲",
                Width = 36,
                Height = 28,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new System.Windows.Thickness(0),
                Margin = new System.Windows.Thickness(2, 0, 5, 0),
                ToolTip = "Refresh"
            };

            var urlBox = new System.Windows.Controls.TextBox
            {
                Text = url,
                Width = 500,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new System.Windows.Thickness(1),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70)),
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                Padding = new System.Windows.Thickness(5, 0, 5, 0)
            };

            toolbar.Children.Add(backBtn);
            toolbar.Children.Add(forwardBtn);
            toolbar.Children.Add(refreshBtn);
            toolbar.Children.Add(urlBox);
            System.Windows.Controls.Grid.SetRow(toolbar, 0);

            var webView = new Microsoft.Web.WebView2.Wpf.WebView2();
            System.Windows.Controls.Grid.SetRow(webView, 1);

            grid.Children.Add(toolbar);
            grid.Children.Add(webView);

            _browserWindow.Content = grid;
            
            _browserWindow.Closed += (s, e) => _browserWindow = null;
            
            _browserWindow.Show();
            
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Navigate(url);

            backBtn.Click += (s, e) => { if (webView.CanGoBack) webView.GoBack(); };
            forwardBtn.Click += (s, e) => { if (webView.CanGoForward) webView.GoForward(); };
            refreshBtn.Click += (s, e) => webView.Reload();
            urlBox.KeyDown += (s, e) => 
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    try { webView.CoreWebView2.Navigate(urlBox.Text); } catch { }
                }
            };
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Error: " + ex.Message, "Browser Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
    }

    private void AddCustomCommand_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Add Custom Command",
            Width = 400,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Add Custom Command", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 0, 0, 15) });

        panel.Children.Add(new TextBlock { Text = "Name:", Foreground = System.Windows.Media.Brushes.LightGray });
        var nameBox = new System.Windows.Controls.TextBox { FontSize = 14, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(nameBox);

        panel.Children.Add(new TextBlock { Text = "Command:", Foreground = System.Windows.Media.Brushes.LightGray });
        var cmdBox = new System.Windows.Controls.TextBox { FontSize = 14, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 15) };
        panel.Children.Add(cmdBox);

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var saveBtn = new System.Windows.Controls.Button { Content = "Add", Width = 80, Height = 32, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Foreground = System.Windows.Media.Brushes.Black, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Height = 32, Background = System.Windows.Media.Brushes.Gray, Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(10, 0, 0, 0) };

        saveBtn.Click += (s, args) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text) && !string.IsNullOrWhiteSpace(cmdBox.Text))
            {
                _customCommands.Add(new CustomCommand { Name = nameBox.Text, Command = cmdBox.Text });
                SaveCommands();
                CommandList.ItemsSource = _customCommands.ToList();
                dialog.Close();
            }
        };
        cancelBtn.Click += (s, args) => dialog.Close();
        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void UrlBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            NavigateToUrl();
        }
    }

    private void GoBtn_Click(object sender, RoutedEventArgs e)
    {
        NavigateToUrl();
    }

    private void OpenExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        var url = MaxUrlBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) return;

        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            url = "https://" + url;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Error opening browser: " + ex.Message);
        }
    }

    private async void OpenInspect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MaxWebView != null && TimelineLogs != null)
            {
                await MaxWebView.EnsureCoreWebView2Async();
                if (WindowState == WindowState.Maximized)
                {
                    TimelineLogs.Text = "DevTools is not available to embed. Use browser's F12 to open DevTools in a separate window.";
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Error opening Inspect: " + ex.Message);
        }
    }

    private async void NavigateToUrl()
    {
        var url = MaxUrlBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) return;

        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            url = "https://" + url;
        }

        try
        {
            if (MaxWebView != null)
            {
                await MaxWebView.EnsureCoreWebView2Async();
                MaxWebView.CoreWebView2.Navigate(url);
                SetupBrowserLogging();
                StartTimelineRefresh();
            }
            _currentBrowserUrl = url;
            if (TimelineStatus != null)
                TimelineStatus.Text = " - " + url;
        }
        catch { }
    }

    private System.Windows.Threading.DispatcherTimer? _timelineTimer;

    private void StartTimelineRefresh()
    {
        _timelineTimer?.Stop();
        _timelineTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timelineTimer.Tick += async (s, e) => await RefreshTimelineLogs();
        _timelineTimer.Start();
    }

    private async Task RefreshTimelineLogs()
    {
        try
        {
            if (MaxWebView == null) return;
            await MaxWebView.EnsureCoreWebView2Async();

            var logs = await MaxWebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__pulsarLogs || [])");
            var net = await MaxWebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__pulsarNet || [])");

            Dispatcher.Invoke(() =>
            {
                if (TimelineLogs != null && !string.IsNullOrEmpty(logs) && logs != "[]")
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(logs);
                    if (parsed != null)
                        TimelineLogs.Text = string.Join("\n", parsed.Take(50));
                }

                if (NetworkLogs != null && !string.IsNullOrEmpty(net) && net != "[]")
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(net);
                    if (parsed != null)
                        NetworkLogs.Text = string.Join("\n", parsed.Take(50));
                }
            });
        }
        catch { }
    }

    private async void SetupBrowserLogging()
    {
        try
        {
            if (MaxWebView == null) return;
            await MaxWebView.EnsureCoreWebView2Async();

            var script = @"
                if (!window.__pulsarSetup) {
                    window.__pulsarLogs = [];
                    window.__pulsarNet = [];
                    window.__pulsarSetup = true;
                    ['log','warn','error','info'].forEach(function(m) {
                        var orig = console[m];
                        console[m] = function() {
                            var msg = '[' + m.toUpperCase() + '] ' + Array.from(arguments).map(function(a) {
                                try { return typeof a === 'object' ? JSON.stringify(a) : String(a); } catch(e) { return String(a); }
                            }).join(' ');
                            window.__pulsarLogs.push(msg);
                            if (window.__pulsarLogs.length > 50) window.__pulsarLogs.shift();
                            orig.apply(console, arguments);
                        };
                    });
                    var ox = XMLHttpRequest.prototype.open;
                    XMLHttpRequest.prototype.open = function(method, url) {
                        window.__pulsarNet.push(method + ' ' + url + ' [pending]');
                        return ox.apply(this, arguments);
                    };
                    var os = XMLHttpRequest.prototype.send;
                    XMLHttpRequest.prototype.send = function() {
                        var t = this;
                        t.addEventListener('load', function() { window.__pulsarNet.push(t.method + ' ' + t.responseURL + ' ' + t.status); });
                        t.addEventListener('error', function() { window.__pulsarNet.push(t.method + ' ERROR'); });
                        return os.apply(t, arguments);
                    };
                }
                'ok';
            ";

            await MaxWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            ShowMaximizedLayout();
        }
        else
        {
            ShowMinimizedLayout();
        }

        PositionWindow();
        LoadProjectSettings();
        if (PortListMax != null)
            PortListMax.ItemsSource = _projects.OrderBy(p => p.Name).ToList();
        RefreshPorts();
        LoadCommands();
        UpdateMetrics();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            ShowMaximizedLayout();
        }
        else
        {
            ShowMinimizedLayout();
        }
    }

    private void ShowMaximizedLayout()
    {
        MinimizedLayout.Visibility = Visibility.Collapsed;
        MaximizedLayout.Visibility = Visibility.Visible;
    }

    private void ShowMinimizedLayout()
    {
        MinimizedLayout.Visibility = Visibility.Visible;
        MaximizedLayout.Visibility = Visibility.Collapsed;
    }

    public MainWindow()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            
            var networkCategory = new PerformanceCounterCategory("Network Interface");
            var instanceNames = networkCategory.GetInstanceNames();
            if (instanceNames.Length > 0)
            {
                var activeInstance = instanceNames.FirstOrDefault(n => !n.Contains("Loopback")) ?? instanceNames[0];
                _netSentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", activeInstance);
                _netReceivedCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", activeInstance);
                _netSentCounter.NextValue();
                _netReceivedCounter.NextValue();
            }

            _cpuCounter.NextValue();
            _diskReadCounter.NextValue();
            _diskWriteCounter.NextValue();
        }
        catch { }

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _updateTimer.Tick += (s, args) => UpdateMetrics();
        _updateTimer.Start();

        _systemMonitorService.Initialize();
    }

    private void UpdateMetrics()
    {
        try
        {
            var cpuVal = _cpuCounter?.NextValue() ?? 0;
            var diskRead = ((_diskReadCounter?.NextValue() ?? 0) / 1024 / 1024);
            var diskWrite = ((_diskWriteCounter?.NextValue() ?? 0) / 1024 / 1024);
            var diskTotal = diskRead + diskWrite;

            var memInfo = GetMemoryInfo();
            var usedMem = memInfo.UsedGB;
            var totalMem = memInfo.TotalGB;
            var ramPercent = memInfo.UsedPercent;

            var topProcesses = GetTopProcesses();

            var netSent = (_netSentCounter?.NextValue() ?? 0) / 1024 / 1024;
            var netReceived = (_netReceivedCounter?.NextValue() ?? 0) / 1024 / 1024;
            var netTotal = netSent + netReceived;

            Dispatcher.Invoke(() =>
            {
                CpuPercent.Text = $"{(int)cpuVal}%";
                CpuBar.Value = cpuVal;
                CpuDetails.Text = $"{(int)cpuVal}%";
                RamPercent.Text = $"{(int)ramPercent}%";
                RamBar.Value = ramPercent;
                RamDetails.Text = $"{usedMem:F1} / {totalMem:F1} GB";
                DiskPercent.Text = $"C: {diskTotal:F1} MB/s";
                DiskBar.Value = diskTotal;
                DiskDetails.Text = $"R:{diskRead:F1} / W:{diskWrite:F1}";
                NetDetails.Text = $"↑{netSent:F1} / ↓{netReceived:F1} MB/s";
                TopProcesses.ItemsSource = topProcesses;
                LastUpdate.Text = $"Updated: {DateTime.Now:HH:mm:ss}";

                if (MaxCpuPercent != null) MaxCpuPercent.Text = $"{(int)cpuVal}%";
                if (MaxCpuBar != null) MaxCpuBar.Value = cpuVal;
                if (MaxCpuDetails != null) MaxCpuDetails.Text = $"{(int)cpuVal}%";
                if (MaxRamPercent != null) MaxRamPercent.Text = $"{(int)ramPercent}%";
                if (MaxRamBar != null) MaxRamBar.Value = ramPercent;
                if (MaxRamDetails != null) MaxRamDetails.Text = $"{usedMem:F1} / {totalMem:F1} GB";
                if (MaxDiskPercent != null) MaxDiskPercent.Text = $"C: {diskTotal:F1} MB/s";
                if (MaxDiskBar != null) MaxDiskBar.Value = diskTotal;
                if (MaxDiskDetails != null) MaxDiskDetails.Text = $"R:{diskRead:F1} / W:{diskWrite:F1}";
                if (MaxNetDetails != null) MaxNetDetails.Text = $"↑{netSent:F1} / ↓{netReceived:F1} MB/s";
                TopProcessesMax.ItemsSource = topProcesses;
            });
        }
        catch { }
    }

    private (double TotalGB, double UsedGB, double UsedPercent) GetMemoryInfo()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(memStatus);
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                var totalBytes = (long)memStatus.ullTotalPhys;
                var availableBytes = (long)memStatus.ullAvailPhys;
                var totalGB = totalBytes / (1024.0 * 1024 * 1024);
                var availableGB = availableBytes / (1024.0 * 1024 * 1024);
                var usedGB = totalGB - availableGB;
                var usedPercent = (usedGB / totalGB) * 100;
                return (totalGB, usedGB, usedPercent);
            }
            return (0, 0, 0);
        }
        catch { return (0, 0, 0); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private List<ProcessInfo> GetTopProcesses()
    {
        var processes = new List<ProcessInfo>();
        try
        {
            var allProcs = System.Diagnostics.Process.GetProcesses();
            foreach (var proc in allProcs)
            {
                try
                {
                    if (proc.WorkingSet64 > 1024 * 1024 * 10)
                    {
                        processes.Add(new ProcessInfo { Name = proc.ProcessName, Percent = (proc.WorkingSet64 / (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) * 100 });
                    }
                }
                catch { }
            }
        }
        catch { }
        return processes.OrderByDescending(p => p.Percent).Take(5).ToList();
    }

    private void PositionWindow()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        Left = screenWidth - Width - 20;
        Top = screenHeight - Height - 50;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _updateTimer.Stop();
        _cpuCounter?.Dispose();
        _ramCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        _netSentCounter?.Dispose();
        _netReceivedCounter?.Dispose();
        _trayIcon?.Dispose();
        _systemMonitorService.Dispose();
    }

    private void NotifBtn_Click(object sender, RoutedEventArgs e)
    {
        var notifWindow = new Window
        {
            Title = "Notifications",
            Width = 350,
            Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
        };
        var panel = new StackPanel { Margin = new Thickness(15) };
        panel.Children.Add(new TextBlock { Text = "Notifications", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)), Margin = new Thickness(0, 0, 0, 10) });
        
        foreach (var n in _notifications.Take(20))
        {
            panel.Children.Add(new TextBlock { Text = n, Foreground = System.Windows.Media.Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) });
        }
        
        notifWindow.Content = panel;
        notifWindow.ShowDialog();
    }

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (SystemContent == null || DevContent == null) return;

        if (SystemTab?.IsChecked == true)
        {
            SystemContent.Visibility = Visibility.Visible;
            DevContent.Visibility = Visibility.Collapsed;
        }
        else
        {
            SystemContent.Visibility = Visibility.Collapsed;
            DevContent.Visibility = Visibility.Visible;
            LoadProjectSettings();
            RefreshPorts();
            LoadCommands();
        }
    }

    private string RunGitCommand(string path, string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd", "/c cd /d \"" + path + "\" && " + command);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd();
            var error = proc?.StandardError.ReadToEnd();
            proc?.WaitForExit();
            return string.IsNullOrWhiteSpace(error) ? output : output + error;
        }
        catch { return "Error"; }
    }

    private void GitPull_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path != null) GitCliOutput.Text += RunGitCommand(path, "git pull") + "\n";
    }

    private void GitPush_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path != null) GitCliOutput.Text += RunGitCommand(path, "git push") + "\n";
    }

    private void GitCommit_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var commitPanel = FindCommitPanelByButton(btn);
        if (commitPanel != null)
        {
            commitPanel.Visibility = commitPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void GitRefresh_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path != null) GitCliOutput.Text += RunGitCommand(path, "git status") + "\n";
    }

    private void GitCliSend_Click(object sender, RoutedEventArgs e)
    {
        var input = FindName("GitCliInput") as System.Windows.Controls.TextBox;
        var output = FindName("GitCliOutput") as System.Windows.Controls.TextBox;
        if (input != null && output != null && !string.IsNullOrWhiteSpace(input.Text))
        {
            output.Text += "> " + input.Text + "\n";
            input.Text = "";
        }
    }

    private void GitCliInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) GitCliSend_Click(sender, e);
    }

    private string _currentCliPath = "";
    private ScrollViewer? _currentCliOutput = null;
    private System.Windows.Controls.TextBox? _currentCliInput = null;

    private void GitProjectCli_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path == null) return;
        
        _currentCliPath = path;
        
        var panel = FindGitCliPanelByButton(btn);
        if (panel != null)
        {
            panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (panel.Visibility == Visibility.Visible)
            {
                _currentCliOutput = panel.FindName("ProjectCliOutput") as ScrollViewer;
                _currentCliInput = panel.FindName("ProjectCliInput") as System.Windows.Controls.TextBox;
                AppendCliOutput("Git CLI ready at: " + path + "\n> ");
            }
        }
    }
    
    private System.Windows.Controls.Border? FindGitCliPanelByButton(System.Windows.Controls.Button btn)
    {
        var current = btn as System.Windows.DependencyObject;
        while (current != null)
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            if (parent == null) break;
            
            if (parent is System.Windows.Controls.Border outerBorder && outerBorder.Name != "GitCliPanel")
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(outerBorder); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(outerBorder, i);
                    if (child is System.Windows.Controls.Grid grid)
                    {
                        for (int j = 0; j < System.Windows.Media.VisualTreeHelper.GetChildrenCount(grid); j++)
                        {
                            var gchild = System.Windows.Media.VisualTreeHelper.GetChild(grid, j);
                            if (gchild is System.Windows.Controls.Border b && b.Name == "GitCliPanel")
                                return b;
                        }
                    }
                }
            }
            current = parent;
        }
        return null;
    }
    
    private T? FindChild<T>(System.Windows.DependencyObject parent) where T : class
    {
        if (parent == null) return null;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
        }
        return null;
    }

    private void AppendCliOutput(string text)
    {
        if (_currentCliOutput != null)
        {
            var content = _currentCliOutput.Content as System.Windows.Controls.TextBlock;
            if (content == null)
            {
                content = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)) };
                _currentCliOutput.Content = content;
            }
            content.Text += text;
        }
    }

    private void ProjectCliRun_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCliInput == null || string.IsNullOrWhiteSpace(_currentCliInput.Text)) return;
        
        var cmd = _currentCliInput.Text;
        _currentCliInput.Text = "";
        AppendCliOutput("> " + cmd + "\n");
        
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd", "/c cd /d \"" + _currentCliPath + "\" && " + cmd);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd();
            var error = proc?.StandardError.ReadToEnd();
            proc?.WaitForExit();
            AppendCliOutput(output + error);
        }
        catch { AppendCliOutput("Error running command\n"); }
    }

    private void GitAdd_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        if (path == null) return;
        
        var output = RunGitCommand(path, "git add .");
        var cliPanel = FindGitCliPanelByButton(btn);
        if (cliPanel != null)
        {
            cliPanel.Visibility = Visibility.Visible;
            AppendCliOutputToPanel(cliPanel, output + "\n");
        }
    }

    private void GitCommitRun_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var path = btn?.Tag?.ToString();
        var commitPanel = FindCommitPanelByButton(btn);
        if (commitPanel == null || path == null) return;
        
        var input = commitPanel.FindName("CommitMessageInput") as System.Windows.Controls.TextBox;
        if (input == null || string.IsNullOrWhiteSpace(input.Text)) return;
        
        var msg = input.Text;
        input.Text = "";
        commitPanel.Visibility = Visibility.Collapsed;
        
        var output = RunGitCommand(path, "git add . && git commit -m \"" + msg + "\"");
        
        var cliPanel = FindGitCliPanelByButton(btn);
        if (cliPanel != null)
        {
            cliPanel.Visibility = Visibility.Visible;
            AppendCliOutputToPanel(cliPanel, output + "\n");
        }
    }

    private void AppendCliOutputToPanel(System.Windows.Controls.Border panel, string text)
    {
        if (panel == null) return;
        var scroll = panel.FindName("ProjectCliOutput") as ScrollViewer;
        if (scroll != null)
        {
            var content = scroll.Content as System.Windows.Controls.TextBlock;
            if (content == null)
            {
                content = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)) };
                scroll.Content = content;
            }
            content.Text += text;
        }
    }

    private System.Windows.Controls.Border? FindCommitPanelByButton(System.Windows.Controls.Button btn)
    {
        var current = btn as System.Windows.DependencyObject;
        while (current != null)
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            if (parent == null) break;
            
            if (parent is System.Windows.Controls.StackPanel sp)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(sp); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(sp, i);
                    if (child is System.Windows.Controls.Border b && b.Name == "CommitPanel")
                        return b;
                }
            }
            current = parent;
        }
        return null;
    }
}