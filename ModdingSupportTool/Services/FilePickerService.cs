using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace ModdingSupportTool.Services;

public class FilePickerService : IFilePickerService
{
    private readonly Func<IStorageProvider?> _storageProviderProvider;

    public FilePickerService(Func<IStorageProvider?> storageProviderProvider)
    {
        _storageProviderProvider = storageProviderProvider;
    }

    public async Task<string?> PickFolderAsync(string? startLocation = null)
    {
        var sp = _storageProviderProvider();
        if (sp == null) return null;

        IStorageFolder? suggested = null;
        if (!string.IsNullOrWhiteSpace(startLocation) && Directory.Exists(startLocation))
        {
            try
            {
                suggested = await sp.TryGetFolderFromPathAsync(startLocation);
            }
            catch { /* ignore */ }
        }

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "폴더 선택",
            SuggestedStartLocation = suggested,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string? title = null, string[]? extensions = null)
    {
        var sp = _storageProviderProvider();
        if (sp == null) return null;

        var fileTypes = new List<FilePickerFileType>();
        if (extensions != null && extensions.Length > 0)
        {
            fileTypes.Add(new FilePickerFileType("대상 파일")
            {
                Patterns = extensions
            });
        }
        fileTypes.Add(FilePickerFileTypes.All);

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title ?? "파일 선택",
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
