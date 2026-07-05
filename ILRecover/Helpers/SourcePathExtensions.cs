namespace ILRecover.Helpers;

public static class SourcePathExtensions
{
    extension(string path)
    {
        public string NormalizePath() => path.Replace('\\', '/');

        public string NormalizePathKey() => path.NormalizePath().ToLowerInvariant();

        public string GetFileStem() => Path.GetFileNameWithoutExtension(path);

        public string GetPrimaryFileStem()
        {
            var stem = path.GetFileStem();
            var index = stem.IndexOf('.');
            return index < 0 ? stem : stem[..index];
        }

        public List<string> GetDirectoryParts()
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return [];

            return directory.NormalizePath()
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
    }

    extension(string value)
    {
        public string SanitizeFileNamePart()
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        }
    }
}
