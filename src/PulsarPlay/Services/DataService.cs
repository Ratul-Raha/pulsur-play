using System.Collections.Generic;
using System.IO;
using PulsarPlay.Models;

namespace PulsarPlay.Services;

public class DataService
{
    private readonly List<CustomCommand> _commands = new();

    public IReadOnlyList<CustomCommand> GetCustomCommands() => _commands;

    public void LoadCommands(string folder)
    {
        _commands.Clear();
        var filePath = Path.Combine(folder, "commands.json");
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<CustomCommand>>(json);
                if (loaded != null) _commands.AddRange(loaded);
            }
            catch { }
        }
    }

    public void SaveCommands(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, "commands.json");
            var json = System.Text.Json.JsonSerializer.Serialize(_commands, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch { }
    }

    public void AddCommand(CustomCommand cmd)
    {
        _commands.Add(cmd);
    }

    public void RemoveCommand(string name)
    {
        _commands.RemoveAll(c => c.Name == name);
    }
}