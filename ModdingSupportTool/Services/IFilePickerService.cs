using System.Threading.Tasks;

namespace ModdingSupportTool.Services;

public interface IFilePickerService
{
    Task<string?> PickFolderAsync(string? startLocation = null);
    Task<string?> PickFileAsync(string? title = null, string[]? extensions = null);
}
