using MRB_Boostrap.Models;

namespace MRB_Boostrap.Services;

public interface IHashValidator
{
    Task<bool> ValidateAsync(List<FileEntry> files, string stagingPath, string hashPath, CancellationToken cancellationToken);
}