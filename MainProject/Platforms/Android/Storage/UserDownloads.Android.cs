using Android.App;
using Android.Content;
using Android.Provider;
using MyLovelyMail.MainProject.Constants;
using AndroidUri = Android.Net.Uri;

namespace MyLovelyMail.MainProject.Storage
{
    public static partial class UserDownloads
    {
        /// <summary>Where published files appear in the phone's Files app.</summary>
        const string PublishedFolder = "Download/" + AppConstants.AppName;

        /// <summary>A staging folder in AppCache; a successful publish leaves nothing behind in it.</summary>
        static partial void ResolvePlatformWriteFolder(ref string? folder)
        {
            folder = Path.Combine(AppPaths.AppCache, "PendingDownloads");
            Directory.CreateDirectory(folder);
        }

        /// <summary>
        /// Copies the staged file — or every file of a staged folder, under the same folder name — into
        /// MediaStore's Downloads collection, which an app may add to without any storage permission,
        /// then deletes the staged copy.
        /// </summary>
        static partial void PublishOnPlatform(string writtenPath, ref string location)
        {
            if (Directory.Exists(writtenPath))
            {
                string folder = $"{PublishedFolder}/{Path.GetFileName(writtenPath)}";
                foreach (string file in Directory.GetFiles(writtenPath))
                    Insert(file, folder);
                Directory.Delete(writtenPath, recursive: true);
                location = folder;
                return;
            }

            location = $"{PublishedFolder}/{Insert(writtenPath, PublishedFolder)}";
            File.Delete(writtenPath);
        }

        /// <summary>Adds one file to the Downloads collection and returns the name Android gave it.</summary>
        static string Insert(string file, string relativeFolder)
        {
            var resolver = Application.Context.ContentResolver!;
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, Path.GetFileName(file));
            values.Put(MediaStore.IMediaColumns.RelativePath, relativeFolder);
            // Pending while the bytes are copied, so no other app sees a half-written file.
            values.Put(MediaStore.IMediaColumns.IsPending, 1);

            AndroidUri uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values)
                ?? throw new IOException($"Android refused to create {Path.GetFileName(file)} in {relativeFolder}.");

            using (var output = resolver.OpenOutputStream(uri) ?? throw new IOException("Android gave no stream to write the download into."))
            using (var input = File.OpenRead(file))
                input.CopyTo(output);

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, values, null, null);

            // MediaStore renames a clashing file ("report (1).pdf"), so the name is read back rather than assumed.
            using var cursor = resolver.Query(uri, [MediaStore.IMediaColumns.DisplayName], null, null, null);
            return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) ?? Path.GetFileName(file) : Path.GetFileName(file);
        }
    }
}
