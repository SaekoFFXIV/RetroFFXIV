using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace RetroXIV;

// An embeddable file browser for picking a ROM. Drawn inline inside the control window's ROM
// tab (not as a separate popup): shows drives, folders, and selectable files; clicking a file
// invokes the selection callback. File extensions are supplied dynamically by the selected
// libretro core.
public sealed class RomBrowser
{
    private static readonly string[] ArchiveExtensions = { ".zip" };
    private const float MinimumEntryListHeight = 160f;

    private readonly Configuration config;
    private readonly Action<string> onSelected;
    private readonly Func<string[]> getRomExtensions;

    private readonly List<string> directories = new();
    private readonly List<string> files = new();

    private string currentDir = string.Empty;
    private string error = string.Empty;
    private bool initialized;

    public RomBrowser(Configuration config, Action<string> onSelected, Func<string[]> getRomExtensions)
    {
        this.config = config;
        this.onSelected = onSelected;
        this.getRomExtensions = getRomExtensions;
    }

    public void DrawContents()
    {
        EnsureInitialized();
        DrawDriveBar();
        DrawPathBar();
        DrawEntries();

        if (!string.IsNullOrEmpty(error))
        {
            ImGui.TextWrapped(error);
        }
    }

    private void EnsureInitialized()
    {
        if (initialized && Directory.Exists(currentDir))
        {
            return;
        }

        var start = !string.IsNullOrEmpty(config.RomDirectory) && Directory.Exists(config.RomDirectory)
            ? config.RomDirectory
            : Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        NavigateTo(start);
        initialized = true;
    }

    private void DrawDriveBar()
    {
        var first = true;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            var name = drive.Name.TrimEnd('\\');
            if (!first)
            {
                ImGui.SameLine();
            }

            first = false;
            if (ImGui.SmallButton($"{name}##drive{name}"))
            {
                NavigateTo(drive.Name);
            }
        }
    }

    private void DrawPathBar()
    {
        if (ImGui.SmallButton("Up##up"))
        {
            var parent = Directory.GetParent(currentDir)?.FullName;
            if (parent != null)
            {
                NavigateTo(parent);
            }
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(currentDir);
    }

    private void DrawEntries()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 4));
        // The ROM tab can have a tall save-state section above this list.
        // Do not let the nested file browser collapse to its one-row
        // remaining height: overflow belongs to the outer tab scroll region.
        var remainingHeight = ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing();
        var entryListHeight = Math.Max(MinimumEntryListHeight, remainingHeight);
        ImGui.BeginChild("##romentries", new Vector2(0, entryListHeight), true);

        // Record the click and act only after the loops: navigating clears and refills these lists,
        // which would throw if done mid-enumeration.
        string? navigateTo = null;
        string? selectedFile = null;

        foreach (var dir in directories)
        {
            if (ImGui.Selectable($"{Path.GetFileName(dir)}/##dir{dir}"))
            {
                navigateTo = dir;
            }
        }

        foreach (var file in files)
        {
            if (ImGui.Selectable($"{Path.GetFileName(file)}##file{file}"))
            {
                selectedFile = file;
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();

        if (navigateTo != null)
        {
            NavigateTo(navigateTo);
        }
        else if (selectedFile != null)
        {
            onSelected(selectedFile);
        }
    }

    private void NavigateTo(string dir)
    {
        currentDir = dir;
        error = string.Empty;
        directories.Clear();
        files.Clear();

        try
        {
            directories.AddRange(Directory.GetDirectories(dir));
            directories.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(dir))
            {
                if (IsSelectable(Path.GetExtension(file)))
                {
                    files.Add(file);
                }
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            error = $"Cannot open this folder: {ex.Message}";
        }
    }

    private bool IsSelectable(string ext) =>
        Array.Exists(getRomExtensions(), e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) ||
        Array.Exists(ArchiveExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
}
