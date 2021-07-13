﻿using AAXClean;
using Dinah.Core;
using Dinah.Core.Diagnostics;
using Dinah.Core.IO;
using Dinah.Core.StepRunner;
using System;
using System.IO;

namespace AaxDecrypter
{
    public interface ISimpleAaxcToM4bConverter
    {
        event EventHandler<AppleTags> RetrievedTags;
        event EventHandler<byte[]> RetrievedCoverArt;
        event EventHandler<TimeSpan> DecryptTimeRemaining;
        event EventHandler<int> DecryptProgressUpdate;
        bool Run();
        string AppName { get; set; }
        string outDir { get; }
        string outputFileName { get; }
        DownloadLicense downloadLicense { get; }
        AaxFile aaxFile { get; }
        byte[] coverArt { get; }
        void SetCoverArt(byte[] coverArt);
        void SetOutputFilename(string outFileName);
    }
    public interface IAdvancedAaxcToM4bConverter : ISimpleAaxcToM4bConverter
    {
        void Cancel();
        bool Step1_CreateDir();
        bool Step2_GetMetadata();
        bool Step3_DownloadAndCombine();
        bool Step5_CreateCue();
        bool Step6_CreateNfo();
        bool Step7_Cleanup();
    }
    public class AaxcDownloadConverter : IAdvancedAaxcToM4bConverter
    {
        public event EventHandler<AppleTags> RetrievedTags;
        public event EventHandler<byte[]> RetrievedCoverArt;
        public event EventHandler<int> DecryptProgressUpdate;
        public event EventHandler<TimeSpan> DecryptTimeRemaining;
        public string AppName { get; set; } = nameof(AaxcDownloadConverter);
        public string outDir { get; private set; }
        public string cacheDir { get; private set; }
        public string outputFileName { get; private set; }
        public DownloadLicense downloadLicense { get; private set; }
        public AaxFile aaxFile { get; private set; }
        public byte[] coverArt { get; private set; }

        private StepSequence steps { get; }
        private NetworkFileStreamPersister nfsPersister;
        private bool isCanceled { get; set; }
        private string jsonDownloadState => Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(outputFileName) + ".json");
        private string tempFile => PathLib.ReplaceExtension(jsonDownloadState, ".aaxc");

        public static AaxcDownloadConverter Create(string cacheDirectory, string outDirectory, DownloadLicense dlLic)
        {
            var converter = new AaxcDownloadConverter(cacheDirectory, outDirectory, dlLic);
            converter.SetOutputFilename(Path.GetTempFileName());
            return converter;
        }

        private AaxcDownloadConverter(string cacheDirectory, string outDirectory, DownloadLicense dlLic)
        {
            ArgumentValidator.EnsureNotNullOrWhiteSpace(outDirectory, nameof(outDirectory));
            ArgumentValidator.EnsureNotNull(dlLic, nameof(dlLic));

            if (!Directory.Exists(outDirectory))
                throw new ArgumentNullException(nameof(cacheDirectory), "Directory does not exist");
            if (!Directory.Exists(outDirectory))
                throw new ArgumentNullException(nameof(outDirectory), "Directory does not exist");

            cacheDir = cacheDirectory;
            outDir = outDirectory;

            steps = new StepSequence
            {
                Name = "Download and Convert Aaxc To M4b",

                ["Step 1: Create Dir"] = Step1_CreateDir,
                ["Step 2: Get Aaxc Metadata"] = Step2_GetMetadata,
                ["Step 3: Download Decrypted Audiobook"] = Step3_DownloadAndCombine,
                ["Step 5: Create Cue"] = Step5_CreateCue,
                ["Step 6: Create Nfo"] = Step6_CreateNfo,
                ["Step 7: Cleanup"] = Step7_Cleanup,
            };

            downloadLicense = dlLic;
        }

        public void SetOutputFilename(string outFileName)
        {
            outputFileName = PathLib.ReplaceExtension(outFileName, ".m4b");
            outDir = Path.GetDirectoryName(outputFileName);

            if (File.Exists(outputFileName))
                File.Delete(outputFileName);
        }

        public void SetCoverArt(byte[] coverArt)
        {
            if (coverArt is null) return;

            this.coverArt = coverArt;
            RetrievedCoverArt?.Invoke(this, coverArt);
        }

        public bool Run()
        {
            var (IsSuccess, Elapsed) = steps.Run();

            if (!IsSuccess)
            {
                Console.WriteLine("WARNING-Conversion failed");
                return false;
            }

            var speedup = (int)(aaxFile.Duration.TotalSeconds / (long)Elapsed.TotalSeconds);
            Console.WriteLine("Speedup is " + speedup + "x realtime.");
            Console.WriteLine("Done");
            return true;
        }

        public bool Step1_CreateDir()
        {
            ProcessRunner.WorkingDir = outDir;
            Directory.CreateDirectory(outDir);

            return !isCanceled;
        }

        public bool Step2_GetMetadata()
        {
            //Get metadata from the file over http
                       
            if (File.Exists(jsonDownloadState))
            {
                try
                {
                    nfsPersister = new NetworkFileStreamPersister(jsonDownloadState);
                    //If More thaan ~1 hour has elapsed since getting the download url, it will expire.
                    //The new url will be to the same file.
                    nfsPersister.NetworkFileStream.SetUriForSameFile(new Uri(downloadLicense.DownloadUrl));
                }
                catch
                {
                    FileExt.SafeDelete(jsonDownloadState);
                    FileExt.SafeDelete(tempFile);
                    nfsPersister = NewNetworkFilePersister();
                }
            }
            else
            {
                nfsPersister = NewNetworkFilePersister();
            }
            nfsPersister.NetworkFileStream.BeginDownloading();

            aaxFile = new AaxFile(nfsPersister.NetworkFileStream);
            coverArt = aaxFile.AppleTags.Cover;

            RetrievedTags?.Invoke(this, aaxFile.AppleTags);
            RetrievedCoverArt?.Invoke(this, coverArt);

            return !isCanceled;
        }
        private NetworkFileStreamPersister NewNetworkFilePersister()
        {
            var headers = new System.Net.WebHeaderCollection();
            headers.Add("User-Agent", downloadLicense.UserAgent);

            NetworkFileStream networkFileStream = new NetworkFileStream(tempFile, new Uri(downloadLicense.DownloadUrl), 0, headers);
            return new NetworkFileStreamPersister(networkFileStream, jsonDownloadState);
        }

        public bool Step3_DownloadAndCombine()
        {
            DecryptProgressUpdate?.Invoke(this, int.MaxValue);

            if (File.Exists(outputFileName))
                FileExt.SafeDelete(outputFileName);

            FileStream outFile = File.OpenWrite(outputFileName);

            aaxFile.DecryptionProgressUpdate += AaxFile_DecryptionProgressUpdate;
            using var decryptedBook = aaxFile.DecryptAaxc(outFile, downloadLicense.AudibleKey, downloadLicense.AudibleIV, downloadLicense.ChapterInfo);
            aaxFile.DecryptionProgressUpdate -= AaxFile_DecryptionProgressUpdate;

            downloadLicense.ChapterInfo = aaxFile.Chapters;

            if (coverArt is not null)
            {
                decryptedBook?.AppleTags?.SetCoverArt(coverArt);
                decryptedBook?.Save();
            }

            decryptedBook?.Close();
            nfsPersister.Dispose();

            DecryptProgressUpdate?.Invoke(this, 0);

            return aaxFile is not null && !isCanceled;
        }

        private void AaxFile_DecryptionProgressUpdate(object sender, DecryptionProgressEventArgs e)
        {
            var duration = aaxFile.Duration;
            double remainingSecsToProcess = (duration - e.ProcessPosition).TotalSeconds;
            double estTimeRemaining = remainingSecsToProcess / e.ProcessSpeed;

            if (double.IsNormal(estTimeRemaining))
                DecryptTimeRemaining?.Invoke(this, TimeSpan.FromSeconds(estTimeRemaining));

            double progressPercent = 100 * e.ProcessPosition.TotalSeconds / duration.TotalSeconds;

            DecryptProgressUpdate?.Invoke(this, (int)progressPercent);
        }

        public bool Step5_CreateCue()
        {
            try
            {
                File.WriteAllText(PathLib.ReplaceExtension(outputFileName, ".cue"), Cue.CreateContents(Path.GetFileName(outputFileName), downloadLicense.ChapterInfo));
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, $"{nameof(Step5_CreateCue)}. FAILED");
            }
            return !isCanceled;
        }

        public bool Step6_CreateNfo()
        {
            try
            {
                File.WriteAllText(PathLib.ReplaceExtension(outputFileName, ".nfo"), NFO.CreateContents(AppName, aaxFile, downloadLicense.ChapterInfo));
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, $"{nameof(Step6_CreateNfo)}. FAILED");
            }
            return !isCanceled;
        }

        public bool Step7_Cleanup()
        {
            FileExt.SafeDelete(jsonDownloadState);
            FileExt.SafeDelete(tempFile);
            return !isCanceled;
        }

        public void Cancel()
        {
            isCanceled = true;
            aaxFile?.Cancel();
        }
    }
}