using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Documents;

public sealed class LocalDocumentStore
{
    private static readonly HashSet<string> AllowedExtensions = [
        ".pdf", ".docx", ".txt", ".md", ".html", ".htm", ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ".mp4", ".mov", ".mkv", ".webm", ".avi"
    ];

    /// <summary>不支持的文件类型提示，SaveAsync 与 CreateUploadSession 共用。</summary>
    public const string SupportedTypesMessage =
        "Supported file types are PDF, DOCX, TXT, MD, HTML, HTM, PNG, JPG, JPEG, WEBP, GIF, MP4, MOV, MKV, WEBM, and AVI.";

    private readonly string _rootPath;
    private readonly long _maxFileSizeBytes;
    private readonly long _maxVideoFileSizeBytes;
    private readonly int _uploadChunkSizeBytes;

    public LocalDocumentStore(IOptions<StorageOptions> options, IWebHostEnvironment environment)
    {
        var storage = options.Value;
        _rootPath = Path.GetFullPath(storage.RootPath, environment.ContentRootPath);
        _maxFileSizeBytes = storage.MaxFileSizeBytes;
        _maxVideoFileSizeBytes = storage.MaxVideoFileSizeBytes;
        _uploadChunkSizeBytes = storage.UploadChunkSizeBytes;
    }

    public async Task<StoredFile> SaveAsync(IFormFile file, CancellationToken cancellationToken)
        => await SaveAsync(file, KnowledgeBaseStore.DefaultKnowledgeBaseId, cancellationToken);

    public async Task<StoredFile> SaveAsync(IFormFile file, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            throw new DocumentUploadException("The uploaded file is empty.");
        }

        var originalFileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(originalFileName) || !AllowedExtensions.Contains(extension))
        {
            throw new DocumentUploadException(SupportedTypesMessage);
        }

        var maxSizeBytes = GetMaxSize(extension);
        if (file.Length > maxSizeBytes)
        {
            throw new DocumentUploadException($"The file exceeds the {maxSizeBytes}-byte limit.");
        }

        var knowledgeBasePath = Path.Combine(_rootPath, knowledgeBaseId.ToString("N"));
        Directory.CreateDirectory(knowledgeBasePath);
        var id = Guid.NewGuid().ToString("N");
        var filePath = Path.Combine(knowledgeBasePath, $"{id}{extension}");

        try
        {
            await using var output = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await file.CopyToAsync(output, cancellationToken);
        }
        catch
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            throw;
        }

        return new StoredFile(Guid.ParseExact(id, "N"), originalFileName, extension, file.ContentType, file.Length, filePath);
    }

    public UploadSession CreateUploadSession(Guid id, Guid knowledgeBaseId, string fileName, string contentType, long sizeBytes)
    {
        var safeFileName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeFileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeFileName) || !AllowedExtensions.Contains(extension))
            throw new DocumentUploadException(SupportedTypesMessage);
        if (sizeBytes <= 0)
            throw new DocumentUploadException("The uploaded file is empty.");

        var maxSizeBytes = GetMaxSize(extension);
        if (sizeBytes > maxSizeBytes)
            throw new DocumentUploadException($"The file exceeds the {maxSizeBytes}-byte limit.");

        var stagingPath = GetStagingPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        using (new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

        return new UploadSession(id, knowledgeBaseId, safeFileName, extension,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            sizeBytes, 0, stagingPath, "Uploading", null);
    }

    public async Task<long> AppendUploadChunkAsync(
        UploadSession session,
        long offset,
        Stream chunk,
        CancellationToken cancellationToken)
    {
        if (offset != session.UploadedBytes)
            throw new DocumentUploadException($"The next chunk must start at byte {session.UploadedBytes}.");
        if (offset < 0 || offset >= session.SizeBytes)
            throw new DocumentUploadException("The chunk offset is outside the upload file.");

        var path = GetStagingPath(session.Id);
        await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, useAsync: true);
        if (output.Length > session.UploadedBytes)
        {
            output.SetLength(session.UploadedBytes);
            await output.FlushAsync(cancellationToken);
        }
        if (output.Length != session.UploadedBytes)
            throw new InvalidOperationException("The staged upload file is shorter than its saved upload offset.");

        output.Position = offset;
        var buffer = new byte[81920];
        long written = 0;
        try
        {
            while (true)
            {
                var bytesRead = await chunk.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (bytesRead == 0) break;
                if (written + bytesRead > _uploadChunkSizeBytes)
                    throw new DocumentUploadException($"A single upload chunk cannot exceed {_uploadChunkSizeBytes} bytes.");
                if (offset + written + bytesRead > session.SizeBytes)
                    throw new DocumentUploadException("The uploaded chunk exceeds the declared file size.");

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                written += bytesRead;
            }

            if (written == 0)
                throw new DocumentUploadException("The uploaded chunk is empty.");

            await output.FlushAsync(cancellationToken);
            return offset + written;
        }
        catch
        {
            output.SetLength(offset);
            await output.FlushAsync(CancellationToken.None);
            throw;
        }
    }

    public StoredFile CompleteUpload(UploadSession session)
    {
        if (session.UploadedBytes != session.SizeBytes)
            throw new DocumentUploadException($"The upload is incomplete: {session.UploadedBytes}/{session.SizeBytes} bytes received.");

        var destinationDirectory = Path.Combine(_rootPath, session.KnowledgeBaseId.ToString("N"));
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, $"{session.Id:N}{session.Extension}");
        var stagingPath = GetStagingPath(session.Id);

        if (File.Exists(stagingPath))
        {
            var stagedFile = new FileInfo(stagingPath);
            if (stagedFile.Length != session.SizeBytes)
                throw new DocumentUploadException("The staged file size does not match the declared upload size.");
            if (File.Exists(destinationPath))
                throw new DocumentUploadException("The destination file already exists for this upload.");
            File.Move(stagingPath, destinationPath);
        }
        else if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length != session.SizeBytes)
        {
            throw new FileNotFoundException("The staged upload file could not be found.", stagingPath);
        }

        return new StoredFile(session.Id, session.FileName, session.Extension, session.ContentType,
            session.SizeBytes, destinationPath);
    }

    public void DeleteUploadStagingFile(Guid id)
    {
        var path = GetStagingPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeleteStoredFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 文件可能正被解析任务占用，忽略删除失败，避免影响接口返回。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public int UploadChunkSizeBytes => _uploadChunkSizeBytes;

    public long GetMaxSize(string extension) => IsVideoExtension(extension) ? _maxVideoFileSizeBytes : _maxFileSizeBytes;

    public static bool IsVideoExtension(string extension) =>
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase);

    private string GetStagingPath(Guid id) => Path.Combine(_rootPath, ".staging", $"{id:N}.part");
}

public sealed record StoredFile(Guid Id, string FileName, string Extension, string ContentType, long SizeBytes, string Path);
