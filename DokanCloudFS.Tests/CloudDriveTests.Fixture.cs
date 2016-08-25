﻿/*
The MIT License(MIT)

Copyright(c) 2015 IgorSoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Moq;
using IgorSoft.CloudFS.Interface;
using IgorSoft.CloudFS.Interface.Composition;
using IgorSoft.CloudFS.Interface.IO;
using IgorSoft.DokanCloudFS.IO;
using IgorSoft.DokanCloudFS.Parameters;

namespace IgorSoft.DokanCloudFS.Tests
{
    public sealed partial class CloudDriveTests
    {
        internal class Fixture
        {
            public const string MOUNT_POINT = "Z";

            public const string SCHEMA = "mock";

            public const string USER_NAME = "IgorDev";

            public const long FREE_SPACE = 64 * 1 << 20;

            public const long USED_SPACE = 36 * 1 << 20;

            private readonly Mock<ICloudGateway> gateway;

            private readonly RootDirectoryInfoContract root;

            private readonly RootName rootName = new RootName(SCHEMA, USER_NAME, MOUNT_POINT);

            public ICloudGateway Gateway => gateway.Object;

            public readonly DirectoryInfoContract TargetDirectory = new DirectoryInfoContract(@"\SubDir", "SubDir", "2015-01-01 10:11:12".ToDateTime(), "2015-01-01 20:21:22".ToDateTime());

            public FileSystemInfoContract[] RootDirectoryItems { get; } = new FileSystemInfoContract[] {
                new DirectoryInfoContract(@"\SubDir", "SubDir", "2015-01-01 10:11:12".ToDateTime(), "2015-01-01 20:21:22".ToDateTime()),
                new DirectoryInfoContract(@"\SubDir2", "SubDir2", "2015-01-01 13:14:15".ToDateTime(), "2015-01-01 23:24:25".ToDateTime()),
                new FileInfoContract(@"\File.ext", "File.ext", "2015-01-02 10:11:12".ToDateTime(), "2015-01-02 20:21:22".ToDateTime(), 16384, "16384".ToHash()),
                new FileInfoContract(@"\SecondFile.ext", "SecondFile.ext", "2015-01-03 10:11:12".ToDateTime(), "2015-01-03 20:21:22".ToDateTime(), 32768, "32768".ToHash()),
                new FileInfoContract(@"\ThirdFile.ext", "ThirdFile.ext", "2015-01-04 10:11:12".ToDateTime(), "2015-01-04 20:21:22".ToDateTime(), 65536, "65536".ToHash())
            };

            public static Fixture Initialize() => new Fixture();

            private Fixture()
            {
                gateway = new Mock<ICloudGateway>(MockBehavior.Strict);
                root = new RootDirectoryInfoContract(Path.DirectorySeparatorChar.ToString(), "2015-01-01 00:00:00".ToDateTime(), "2015-01-01 00:00:00".ToDateTime()) {
                    Drive = new DriveInfoContract(MOUNT_POINT, FREE_SPACE, USED_SPACE)
                };
            }

            public CloudDrive Create(string apiKey, string encryptionKey)
            {
                return new CloudDrive(new RootName(SCHEMA, USER_NAME, MOUNT_POINT), gateway.Object, new CloudDriveParameters() { ApiKey = apiKey, EncryptionKey = encryptionKey });
            }

            public void SetupGetDrive(string apiKey, IDictionary<string, string> parameters)
            {
                gateway
                    .Setup(g => g.GetDrive(rootName, apiKey, parameters))
                    .Returns(root.Drive);
            }

            public void SetupGetRoot(string apiKey, IDictionary<string, string> parameters)
            {
                gateway
                    .Setup(g => g.GetRoot(rootName, apiKey, parameters))
                    .Returns(root);
            }

            public void SetupGetRootDirectoryItems(string encryptionKey = null)
            {
                gateway
                    .Setup(g => g.GetChildItem(rootName, new DirectoryId(Path.DirectorySeparatorChar.ToString())))
                    .Returns(RootDirectoryItems);

                if (!string.IsNullOrEmpty(encryptionKey))
                    foreach (var fileInfo in RootDirectoryItems.OfType<FileInfoContract>())
                        using (var rawStream = new MemoryStream(Enumerable.Repeat<byte>(0, (int)fileInfo.Size).ToArray()))
                            gateway
                                .SetupSequence(g => g.GetContent(rootName, fileInfo.Id))
                                .Returns(rawStream.EncryptOrPass(encryptionKey));
            }

            public void SetupGetContent(FileInfoContract source, byte[] content, string encryptionKey = null, bool canSeek = true)
            {
                var stream = new MemoryStream(content);
                if (!string.IsNullOrEmpty(encryptionKey)) {
                    var buffer = new MemoryStream();
                    SharpAESCrypt.SharpAESCrypt.Encrypt(encryptionKey, stream, buffer);
                    buffer.Seek(0, SeekOrigin.Begin);
                    stream = buffer;
                }
                if (!canSeek)
                    stream = new LinearReadMemoryStream(stream);
                gateway
                    .Setup(g => g.GetContent(rootName, source.Id))
                    .Returns(stream);
            }

            public void SetupSetContent(FileInfoContract target, byte[] content, string encryptionKey)
            {
                Func<Stream, bool> checkContent = stream => {
                    if (!string.IsNullOrEmpty(encryptionKey)) {
                        var buffer = new MemoryStream();
                        SharpAESCrypt.SharpAESCrypt.Decrypt(encryptionKey, stream, buffer);
                        buffer.Seek(0, SeekOrigin.Begin);
                        return buffer.Contains(content);
                    }
                    return stream.Contains(content);
                };
                gateway
                    .Setup(g => g.SetContent(rootName, target.Id, It.Is<Stream>(s => checkContent(s)), It.IsAny<IProgress<ProgressValue>>()));
            }

            public void SetupMoveDirectoryOrFile(FileSystemInfoContract directoryOrFile, DirectoryInfoContract target)
            {
                SetupMoveItem(directoryOrFile, directoryOrFile.Name, target);
            }

            public void SetupRenameDirectoryOrFile(FileSystemInfoContract directoryOrFile, string name)
            {
                SetupMoveItem(directoryOrFile, name, (directoryOrFile as DirectoryInfoContract)?.Parent ?? (directoryOrFile as FileInfoContract)?.Directory ?? null);
            }

            private void SetupMoveItem(FileSystemInfoContract directoryOrFile, string name, DirectoryInfoContract target)
            {
                gateway
                    .Setup(g => g.MoveItem(rootName, directoryOrFile.Id, name, target.Id))
                    .Returns((RootName _rootName, FileSystemId source, string movePath, DirectoryId destination) => {
                        var directorySource = source as DirectoryId;
                        if (directorySource != null)
                            return new DirectoryInfoContract(source.Value, movePath, directoryOrFile.Created, directoryOrFile.Updated) { Parent = target };
                        var fileSource = source as FileId;
                        if (fileSource != null)
                            return new FileInfoContract(source.Value, movePath, directoryOrFile.Created, directoryOrFile.Updated, ((FileInfoContract)directoryOrFile).Size, ((FileInfoContract)directoryOrFile).Hash) { Directory = target };
                        throw new InvalidOperationException($"Unsupported type '{source.GetType().Name}'".ToString(CultureInfo.CurrentCulture));
                    });
            }

            public void SetupNewDirectoryItem(DirectoryInfoContract parent, string directoryName)
            {
                gateway
                    .Setup(g => g.NewDirectoryItem(rootName, parent.Id, directoryName))
                    .Returns(new DirectoryInfoContract(parent.Id + Path.DirectorySeparatorChar.ToString() + directoryName, directoryName, DateTimeOffset.Now, DateTimeOffset.Now));
            }

            public void SetupNewFileItem(DirectoryInfoContract parent, string fileName, byte[] content, string encryptionKey)
            {
                Func<Stream, bool> checkContent = stream => {
                    if (!string.IsNullOrEmpty(encryptionKey)) {
                        var buffer = new MemoryStream();
                        SharpAESCrypt.SharpAESCrypt.Decrypt(encryptionKey, stream, buffer);
                        buffer.Seek(0, SeekOrigin.Begin);
                        return buffer.Contains(content);
                    }
                    return stream.Contains(content);
                };
                gateway
                    .Setup(g => g.NewFileItem(rootName, parent.Id, fileName, It.Is<Stream>(s => checkContent(s)), It.IsAny<IProgress<ProgressValue>>()))
                    .Returns(new FileInfoContract(parent.Id + Path.DirectorySeparatorChar.ToString() + fileName, fileName, DateTimeOffset.Now, DateTimeOffset.Now, content.Length, Encoding.Default.GetString(content).ToHash()));
            }

            public void SetupRemoveDirectoryOrFile(FileSystemInfoContract directoryOrFile, bool recurse)
            {
                gateway
                    .Setup(g => g.RemoveItem(rootName, directoryOrFile.Id, recurse));
            }

            public void VerifyAll()
            {
                gateway.VerifyAll();
            }
        }
    }
}
