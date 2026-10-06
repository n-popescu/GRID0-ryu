using LibHac.Bcat;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Bcat;
using Ryujinx.Horizon.Sdk.Sf;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using System;
using System.IO;

namespace Ryujinx.Horizon.Bcat.Ipc
{
    /// <summary>A title's delivery cache served from <see cref="LocalDeliveryCache"/>.</summary>
    partial class LocalDeliveryCacheStorageService : IDeliveryCacheStorageService
    {
        private readonly string _titlePath;

        public LocalDeliveryCacheStorageService(string titlePath)
        {
            _titlePath = titlePath;
        }

        [CmifCommand(0)]
        public Result CreateFileService(out IDeliveryCacheFileService service)
        {
            service = new LocalDeliveryCacheFileService(_titlePath);

            return Result.Success;
        }

        [CmifCommand(1)]
        public Result CreateDirectoryService(out IDeliveryCacheDirectoryService service)
        {
            service = new LocalDeliveryCacheDirectoryService(_titlePath);

            return Result.Success;
        }

        [CmifCommand(10)]
        public Result EnumerateDeliveryCacheDirectory(out int count, [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<DirectoryName> directoryNames)
        {
            string[] names = LocalDeliveryCache.Directories(_titlePath);
            count = Math.Min(names.Length, directoryNames.Length);

            for (int i = 0; i < count; i++)
            {
                LocalDeliveryCache.FillName(ref directoryNames[i], names[i]);
            }

            return Result.Success;
        }
    }

    partial class LocalDeliveryCacheDirectoryService : IDeliveryCacheDirectoryService
    {
        private readonly string _titlePath;
        private string _directoryPath;

        public LocalDeliveryCacheDirectoryService(string titlePath)
        {
            _titlePath = titlePath;
        }

        [CmifCommand(0)]
        public Result Open(DirectoryName directoryName)
        {
            if (_directoryPath != null)
            {
                return BcatResult.AlreadyOpen;
            }

            string name = LocalDeliveryCache.ToName(ref directoryName);
            string path = Path.Combine(_titlePath, name);

            if (!LocalDeliveryCache.IsSafeName(name) || !Directory.Exists(path))
            {
                return BcatResult.NotFound;
            }

            _directoryPath = path;

            return Result.Success;
        }

        [CmifCommand(1)]
        public Result Read(out int entriesRead, [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<DeliveryCacheDirectoryEntry> entriesBuffer)
        {
            entriesRead = 0;

            if (_directoryPath == null)
            {
                return BcatResult.NotOpen;
            }

            string[] files = LocalDeliveryCache.Files(_directoryPath);
            entriesRead = Math.Min(files.Length, entriesBuffer.Length);

            for (int i = 0; i < entriesRead; i++)
            {
                FileName name = default;
                Digest digest = default;
                LocalDeliveryCache.FillName(ref name, Path.GetFileName(files[i]));
                LocalDeliveryCache.FillDigest(ref digest, files[i]);
                entriesBuffer[i] = new DeliveryCacheDirectoryEntry(in name, new FileInfo(files[i]).Length, in digest);
            }

            return Result.Success;
        }

        [CmifCommand(2)]
        public Result GetCount(out int count)
        {
            count = 0;

            if (_directoryPath == null)
            {
                return BcatResult.NotOpen;
            }

            count = LocalDeliveryCache.Files(_directoryPath).Length;

            return Result.Success;
        }
    }

    partial class LocalDeliveryCacheFileService : IDeliveryCacheFileService
    {
        private readonly string _titlePath;
        private string _filePath;

        public LocalDeliveryCacheFileService(string titlePath)
        {
            _titlePath = titlePath;
        }

        [CmifCommand(0)]
        public Result Open(DirectoryName directoryName, FileName fileName)
        {
            if (_filePath != null)
            {
                return BcatResult.AlreadyOpen;
            }

            string directory = LocalDeliveryCache.ToName(ref directoryName);
            string file = LocalDeliveryCache.ToName(ref fileName);
            string path = Path.Combine(_titlePath, directory, file);

            if (!LocalDeliveryCache.IsSafeName(directory) || !LocalDeliveryCache.IsSafeName(file) || !File.Exists(path))
            {
                return BcatResult.NotFound;
            }

            _filePath = path;

            return Result.Success;
        }

        [CmifCommand(1)]
        public Result Read(long offset, out long bytesRead, [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> data)
        {
            bytesRead = 0;

            if (_filePath == null)
            {
                return BcatResult.NotOpen;
            }

            using FileStream stream = File.OpenRead(_filePath);

            if (offset < 0 || offset > stream.Length)
            {
                return BcatResult.InvalidArgument;
            }

            stream.Position = offset;
            int total = 0;
            int read;

            while (total < data.Length && (read = stream.Read(data[total..])) > 0)
            {
                total += read;
            }

            bytesRead = total;

            return Result.Success;
        }

        [CmifCommand(2)]
        public Result GetSize(out long size)
        {
            size = 0;

            if (_filePath == null)
            {
                return BcatResult.NotOpen;
            }

            size = new FileInfo(_filePath).Length;

            return Result.Success;
        }

        [CmifCommand(3)]
        public Result GetDigest(out Digest digest)
        {
            digest = default;

            if (_filePath == null)
            {
                return BcatResult.NotOpen;
            }

            LocalDeliveryCache.FillDigest(ref digest, _filePath);

            return Result.Success;
        }
    }
}
