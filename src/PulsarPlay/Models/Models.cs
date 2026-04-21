using System.Collections.Generic;

namespace PulsarPlay.Models;

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
    public string VercelProjectId { get; set; } = "";
    public string VercelOrgId { get; set; } = "";
    public string VercelProjectName { get; set; } = "";
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

public class AppSettings
{
    public int RamWarning { get; set; } = 80;
    public int RamCritical { get; set; } = 90;
    public int CpuWarning { get; set; } = 80;
    public int CpuCritical { get; set; } = 95;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
}