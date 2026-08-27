# Linux and macOS Build & Distribution Guide

DogdouSpec is designed as a portable, native-first tool targeting .NET 10 and C# 14. This document provides guidance for building, packaging, and running DogdouSpec on Linux and macOS environments.

---

## 1. Platform Support & Support Tier Notice

> [!NOTE]
> **Platform Support Policy (Best-Effort)**:
> - **Primary Tier (Fully Verified)**: Windows x64 (Native AOT, WinGet package distribution, PowerShell wrapper/CLI verification suites).
> - **Secondary Tier (Best-Effort / Community)**:
>   - **Linux x64** (`linux-x64`)
>   - **macOS ARM64** (`osx-arm64`)
>
> Automated cross-platform builds are compiled via GitHub Actions matrix runners (`ubuntu-latest` and `macos-latest`). Due to the absence of dedicated physical Linux/macOS release certification environments, non-Windows platforms are provided on a **best-effort** basis without official SLA commitments or commercial support guarantees.

---

## 2. Building from Source

### Prerequisites
- .NET 10 SDK (`10.0.100` or compatible `10.0.*`).
- Native AOT prerequisites:
  - **Linux (Ubuntu/Debian)**: `clang`, `zlib1g-dev`, `build-essential`
    ```bash
    sudo apt-get update && sudo apt-get install -y clang zlib1g-dev build-essential
    ```
  - **macOS**: Xcode Command Line Tools
    ```bash
    xcode-select --install
    ```

### Compilation

#### Option A: Native AOT Single-File Executable (Recommended)

- **Linux x64**:
  ```bash
  dotnet publish src/DogdouSpec.Cli/DogdouSpec.Cli.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o publish-out/linux-x64
  ```

- **macOS ARM64 (Apple Silicon)**:
  ```bash
  dotnet publish src/DogdouSpec.Cli/DogdouSpec.Cli.csproj \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o publish-out/osx-arm64
  ```

#### Option B: Direct .NET CLI Execution (Developer Mode)

You can run DogdouSpec directly using the .NET runtime without AOT compilation:
```bash
dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -- <command> [options]
```

---

## 3. Running and Installation

1. Make the binary executable:
   ```bash
   chmod +x publish-out/linux-x64/dogdouspec
   # or for macOS:
   chmod +x publish-out/osx-arm64/dogdouspec
   ```

2. Move to a directory in your `$PATH` (e.g., `~/.local/bin` or `/usr/local/bin`):
   ```bash
   mv publish-out/linux-x64/dogdouspec ~/.local/bin/
   ```

3. Verify installation:
   ```bash
   dogdouspec --version
   ```

---

## 4. Release Packaging Structure

The automated GitHub Actions release pipeline (`.github/workflows/release.yml`) generates:

| Target Platform | Package Name | Contents |
|---|---|---|
| **Linux x64** | `dogdouspec-linux-x64.tar.gz` | `dogdouspec` (Executable binary) |
| **macOS ARM64** | `dogdouspec-osx-arm64.tar.gz` | `dogdouspec` (Executable binary) |
| **Windows x64** | `dogdouspec-win-x64.zip` | `dogdouspec.exe` (Executable binary) |

Each archive is accompanied by a standard `.sha256` checksum file in the GitHub Release assets.