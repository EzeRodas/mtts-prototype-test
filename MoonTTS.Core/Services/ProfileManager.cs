using System.Text.Json;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Core.Services;

/* File Structure:
        - GetProfiles() : IReadOnlyList<HotkeyProfile>
        - GetActiveProfiles() : HotkeyProfile
        - CreateProfile(string name) : Result
        - RenameProfile(string oldName, string newName) : Result
        - SetActiveProfile(string name) : Result
        - DeleteProfile(string name) : Result
        - DeleteProfileFolder(string folderName) : void
*/

public class ProfileManager
{
    public const string DefaultProfileName = "Default";

    private readonly IConfigurationRepository _repo;
    
    private readonly string _hotkeysDir;

    public event Action<HotkeyProfile>? ActiveProfileChanged;
    public event Action? ProfilesListChanged;
    public event Action<HotkeyProfile>? ProfileDeleted;

    /* Initializes the profile manager and loads saved profiles from disk */
    public ProfileManager(IConfigurationRepository repo)
    {
        _repo = repo;
        _hotkeysDir = Path.Combine(PathResolver.GetConfigDirectory(), "audio", "profiles");
        Directory.CreateDirectory(_hotkeysDir);

        /* Passes the repository thread so the UI can update */
        _repo.ProfilesChanged += () => ProfilesListChanged?.Invoke();
        
        
    }

    /* Returns the profile data */
    private ProfileDataStore Store => _repo.ProfileData;
    
    /* Returns a read-only list of all available hotkey profiles */
    public IReadOnlyList<HotkeyProfile> GetProfiles() => Store.Profiles.AsReadOnly();

    /* Retrieves the currently active hotkey profile, creating a default one if necessary */
    public HotkeyProfile GetActiveProfiles()
    {
        var profile = Store.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, Store.ActiveProfileName, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
        {
            profile = Store.Profiles[0];
            _repo.SetActiveProfile(profile.Name);
        }

        return profile;
    }

    /* Creates a new hotkey profile with a unique folder name */
    public Result CreateProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Profile name cannot be empty.");

        /* Trims spaces and checks if that profile already exists */
        string trimmedName = name.Trim();
        if (Store.Profiles.Any(p => string.Equals(p.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure($"A profile named '{trimmedName}' already exists.");

        string baseFolder = PathSanitizer.SanitizeFolderName(trimmedName);
        string uniqueFolder = baseFolder;
        int counter = 1;

        /* Prevents collisions when creating default folder names if the user sets an invalid name */
        while (Store.Profiles.Any(p => 
                   string.Equals(p.FolderName, uniqueFolder, StringComparison.OrdinalIgnoreCase)) ||
               Directory.Exists(Path.Combine(_hotkeysDir, uniqueFolder)))
        {
            uniqueFolder = $"{baseFolder}({counter++})";
        }

        var newProfile = new HotkeyProfile
        {
            Name = trimmedName,
            FolderName = uniqueFolder
        };
        
        var newStore = new ProfileDataStore
        {
            ActiveProfileName = Store.ActiveProfileName,
            Profiles = Store.Profiles.ToList()
        };
        newStore.Profiles.Add(newProfile);
        
        Directory.CreateDirectory(Path.Combine(_hotkeysDir, uniqueFolder));
        
        _repo.SaveProfileData(newStore);
        return Result.Success();
    }

    /* Sets the active hotkey profile by name */
    public Result SetActiveProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Profile name cannot be empty.");

        /* Searches for the profile whose name matches */
        var target = Store.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (target == null)
            return Result.Failure($"Profile '{name}' was not found.");

        if (string.Equals(Store.ActiveProfileName, target.Name, StringComparison.OrdinalIgnoreCase))
            return Result.Success();

        _repo.SetActiveProfile(target.Name);
        ActiveProfileChanged?.Invoke(target);
        return Result.Success();
    }

    /* Renames an existing hotkey profile and updates its folder on disk */
    public Result RenameProfile(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName))
            return Result.Failure("Current profile name cannot be empty.");
        if (string.IsNullOrWhiteSpace(newName))
            return Result.Failure("New profile name cannot be empty.");

        string trimmedOld = oldName.Trim();
        string trimmedNew = newName.Trim();

        var target = Store.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, trimmedOld, StringComparison.OrdinalIgnoreCase));

        if (target == null)
            return Result.Failure($"Profile '{trimmedOld}' was not found.");

        if (string.Equals(target.Name, trimmedNew, StringComparison.Ordinal))
            return Result.Success();

        if (Store.Profiles.Any(p =>
                !ReferenceEquals(p, target) &&
                string.Equals(p.Name, trimmedNew, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure($"A profile named '{trimmedNew}' already exists.");
        }

        string baseFolder = PathSanitizer.SanitizeFolderName(trimmedNew);
        string uniqueFolder = baseFolder;
        int counter = 1;

        while (Store.Profiles.Any(p =>
                   !ReferenceEquals(p, target) &&
                   string.Equals(p.FolderName, uniqueFolder, StringComparison.OrdinalIgnoreCase)) ||
               (!string.Equals(target.FolderName, uniqueFolder, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(_hotkeysDir, uniqueFolder))))
        {
            uniqueFolder = $"{baseFolder}({counter++})";
        }

        string oldFolderPath = Path.Combine(_hotkeysDir, target.FolderName);
        string newFolderPath = Path.Combine(_hotkeysDir, uniqueFolder);

        if (!string.Equals(oldFolderPath, newFolderPath, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.Exists(oldFolderPath))
                    Directory.Move(oldFolderPath, newFolderPath);
                else
                    Directory.CreateDirectory(newFolderPath);
            }
            catch (Exception ex)
            {
                return Result.Failure($"Failed to rename profile folder: {ex.Message}", ex);
            }
        }

        bool wasActive = string.Equals(Store.ActiveProfileName, target.Name, StringComparison.OrdinalIgnoreCase);

        var newStore = new ProfileDataStore
        {
            ActiveProfileName = wasActive ? trimmedNew : Store.ActiveProfileName,
            Profiles = Store.Profiles.Select(p => ReferenceEquals(p, target)
                ? new HotkeyProfile
                {
                    Name = trimmedNew,
                    FolderName = uniqueFolder,
                    Entries = p.Entries
                }
                : p).ToList()
        };

        _repo.SaveProfileData(newStore);
        if (wasActive)
        {
            var updated = newStore.Profiles.First(p =>
                string.Equals(p.Name, trimmedNew, StringComparison.OrdinalIgnoreCase));
            ActiveProfileChanged?.Invoke(updated);
        }

        return Result.Success();
    }

    /* Deletes a profile by name and falls back to default profile if the deleted profile was active */
    public Result DeleteProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Profile name cannot be empty.");

        var target = Store.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (target == null)
            return Result.Failure($"Profile '{name}' was not found.");

        var newStore = new ProfileDataStore
        {
            ActiveProfileName = Store.ActiveProfileName,
            Profiles = Store.Profiles.ToList()
        };

        /* If there's only one profile left, creates a new "Default" profile and sets it as active */
        if (Store.Profiles.Count <= 1)
        {
            newStore.Profiles.Remove(target);
            DeleteProfileFolder(target.FolderName);
            ProfileDeleted?.Invoke(target);
            
            newStore.Profiles.Add(new HotkeyProfile { Name = DefaultProfileName, FolderName = DefaultProfileName});
            newStore.ActiveProfileName = DefaultProfileName;
            
            _repo.SaveProfileData(newStore);
            _repo.SetActiveProfile(DefaultProfileName);
            ActiveProfileChanged?.Invoke(newStore.Profiles[0]);
            return Result.Success();
        }
        
        bool wasActive = string.Equals(Store.ActiveProfileName, target.Name, StringComparison.OrdinalIgnoreCase);
        newStore.Profiles.RemoveAll(p => string.Equals(p.Name, target.Name, StringComparison.OrdinalIgnoreCase));

        DeleteProfileFolder(target.FolderName);

        if (wasActive)
        {
            var fallback = newStore.Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, DefaultProfileName, StringComparison.OrdinalIgnoreCase)) ?? newStore.Profiles[0];

            newStore.ActiveProfileName = fallback.Name;
            _repo.SaveProfileData(newStore);
            _repo.SetActiveProfile(fallback.Name);
            ActiveProfileChanged?.Invoke(fallback);
        }
        else
        {
            _repo.SaveProfileData(newStore);
        }
        
        ProfileDeleted?.Invoke(target);
        return Result.Success();
    }
    
    /* Deletes a profile's folder and all contained audio and configuration files from disk */
    private void DeleteProfileFolder(string folderName)
    {
        string folderPath = Path.Combine(_hotkeysDir, folderName);
        if (Directory.Exists(folderPath))
        {
            try { Directory.Delete(folderPath, recursive: true); } catch { /* Ignore if locked */ }
        }
    }
}