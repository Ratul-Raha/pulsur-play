using System;
using System.Collections.Generic;
using System.IO;
using PulsarPlay.Models;

namespace PulsarPlay.Services;

public class ProjectService
{
    private readonly List<ProjectInfo> _projects = new();

    public IReadOnlyList<ProjectInfo> GetProjects() => _projects;

    public void LoadProjects(string folder)
    {
        _projects.Clear();
        var filePath = Path.Combine(folder, "projects.json");
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<ProjectInfo>>(json);
                if (loaded != null) _projects.AddRange(loaded);
            }
            catch { }
        }
    }

    public void SaveProjects(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, "projects.json");
            var json = System.Text.Json.JsonSerializer.Serialize(_projects, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch { }
    }

    public void AddProject(ProjectInfo project)
    {
        _projects.Add(project);
    }

    public void RemoveProject(string path)
    {
        _projects.RemoveAll(p => p.Path == path);
    }
}