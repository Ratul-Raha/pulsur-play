# Pulsar Play

A Windows desktop application for developers to monitor system health and manage development projects.

## Features

- **System Health Monitoring**
  - CPU usage
  - RAM usage
  - Disk I/O
  - Top processes by memory usage

- **Project Management**
  - Add/remove development projects
  - Start/stop projects with one click
  - Open project in browser
  - Edit project settings

- **Commands**
  - Save and run custom commands per project
  - Quick access terminal commands

- **Embedded Browser**
  - WebView2 integration for viewing project UIs

## Installation

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (usually pre-installed on Windows 10/11)

### Build from Source

```bash
# Clone the repository
git clone https://github.com/yourusername/pulsar-play.git
cd pulsar-play

# Build
dotnet build

# Run
dotnet run
```

### Build Release

```bash
dotnet build -c Release
```

The executable will be at:
```
bin\Release\net8.0-windows\PulsarPlay.exe
```

## Usage

1. Launch `PulsarPlay.exe`
2. The app appears in the bottom-right corner of your screen
3. Switch between **System** and **Dev** tabs
4. Add projects via the **+** button in the Dev tab
5. Click **▶** to start a project
6. Click **URL** to open the project in your browser

## Keyboard Shortcuts

- Window is draggable from the title bar
- Minimize to taskbar with `_` button
- Close with `x` button

## Configuration

Projects are stored in:
```
%APPDATA%\PulsarPlay\projects.json
```

Commands are stored in:
```
%APPDATA%\PulsarPlay\commands.json
```

## Contribution

Contributions are welcome! Here's how you can help:

### 1. Fork the Repository

Click the "Fork" button on GitHub or:
```bash
git fork https://github.com/yourusername/pulsar-play.git
```

### 2. Create a Feature Branch

```bash
git checkout -b feature/your-feature-name
```

### 3. Make Changes

Edit the code and ensure it builds:
```bash
dotnet build
```

### 4. Commit Your Changes

```bash
git add .
git commit -m "Add your feature description"
```

### 5. Push to GitHub

```bash
git push origin feature/your-feature-name
```

### 6. Create a Pull Request

1. Go to your fork on GitHub
2. Click "Compare & pull request"
3. Describe your changes
4. Submit

### Code Style

- Follow existing C# conventions
- Use meaningful variable names
- Add comments for complex logic

### Reporting Issues

If you find bugs or have feature requests:
1. Check existing issues first
2. Create a new issue with:
   - Clear title
   - Description of the problem/suggestion
   - Steps to reproduce (if bug)
   - Screenshots if relevant

## License

MIT License - see LICENSE file for details.

## Acknowledgments

- Built with .NET 8 and WPF
- Uses Microsoft WebView2