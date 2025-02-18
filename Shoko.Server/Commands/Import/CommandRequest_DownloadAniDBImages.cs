﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Xml;
using AniDBAPI;
using Shoko.Commons.Extensions;
using Shoko.Commons.Properties;
using Shoko.Commons.Queue;
using Shoko.Commons.Utils;
using Shoko.Models.Enums;
using Shoko.Models.Queue;
using Shoko.Models.Server;
using Shoko.Server.AniDB_API;
using Shoko.Server.Extensions;
using Shoko.Server.ImageDownload;
using Shoko.Server.Models;
using Shoko.Server.Repositories;
using Shoko.Server.Settings;

namespace Shoko.Server.Commands
{
    [Command(CommandRequestType.DownloadAniDBImages)]
    public class CommandRequest_DownloadAniDBImages : CommandRequestImplementation
    {
        public int AnimeID { get; set; }
        public bool ForceDownload { get; set; }

        public override CommandRequestPriority DefaultPriority => CommandRequestPriority.Priority1;

        public override QueueStateStruct PrettyDescription => new QueueStateStruct
        {
            queueState = QueueStateEnum.DownloadImage,
            extraParams = new[] { Resources.Command_ValidateAllImages_AniDBPosters, AnimeID.ToString() }
        };

        public QueueStateStruct PrettyDescriptionCharacters => new QueueStateStruct
        {
            queueState = QueueStateEnum.DownloadImage,
            extraParams = new[] { Resources.Command_ValidateAllImages_AniDBCharacters, AnimeID.ToString() }
        };

        public QueueStateStruct PrettyDescriptionCreators => new QueueStateStruct
        {
            queueState = QueueStateEnum.DownloadImage,
            extraParams = new[] { Resources.Command_ValidateAllImages_AniDBSeiyuus, AnimeID.ToString() }
        };

        public CommandRequest_DownloadAniDBImages()
        {
        }

        public CommandRequest_DownloadAniDBImages(int animeID, bool forced)
        {
            AnimeID = animeID;
            ForceDownload = forced;
            Priority = (int) DefaultPriority;

            GenerateCommandID();
        }

        public override void ProcessCommand()
        {
            logger.Info("Processing CommandRequest_DownloadAniDBImages: {0}", AnimeID);

            AniDbRateLimiter.Instance.EnsureRate();
            try
            {
                List<ImageEntityType> types = new List<ImageEntityType>
                {
                    ImageEntityType.AniDB_Cover,
                    ImageEntityType.AniDB_Character,
                    ImageEntityType.AniDB_Creator
                };
                foreach (var EntityTypeEnum in types)
                {
                    List<string> downloadURLs = new List<string>();
                    List<string> fileNames = new List<string>();
                    switch (EntityTypeEnum)
                    {
                        case ImageEntityType.AniDB_Cover:
                            SVR_AniDB_Anime anime = RepoFactory.AniDB_Anime.GetByAnimeID(AnimeID);
                            if (anime == null)
                            {
                                logger.Warn(
                                    $"AniDB poster image failed to download: Can't find AniDB_Anime with ID: {AnimeID}");
                                return;
                            }

                            downloadURLs.Add(string.Format(Constants.URLS.AniDB_Images, anime.Picname));
                            fileNames.Add(anime.PosterPath);
                            break;

                        case ImageEntityType.AniDB_Character:
                            if (!ServerSettings.Instance.AniDb.DownloadCharacters) continue;
                            var chrs = (from xref1 in RepoFactory.AniDB_Anime_Character.GetByAnimeID(AnimeID)
                                    select RepoFactory.AniDB_Character.GetByCharID(xref1.CharID))
                                .Where(a => !string.IsNullOrEmpty(a?.PicName))
                                .DistinctBy(a => a.CharID)
                                .ToList();
                            if (chrs == null || chrs.Count == 0)
                            {
                                logger.Warn(
                                    $"AniDB Character image failed to download: Can't find Character for anime: {AnimeID}");
                                return;
                            }

                            foreach (var chr in chrs)
                            {
                                downloadURLs.Add(string.Format(Constants.URLS.AniDB_Images, chr.PicName));
                                fileNames.Add(chr.GetPosterPath());
                            }

                            ShokoService.CmdProcessorGeneral.QueueState = PrettyDescriptionCharacters;
                            break;

                        case ImageEntityType.AniDB_Creator:
                            if (!ServerSettings.Instance.AniDb.DownloadCreators) continue;

                            var creators = (from xref1 in RepoFactory.AniDB_Anime_Character.GetByAnimeID(AnimeID)
                                    from xref2 in RepoFactory.AniDB_Character_Seiyuu.GetByCharID(xref1.CharID)
                                    select RepoFactory.AniDB_Seiyuu.GetBySeiyuuID(xref2.SeiyuuID))
                                .Where(a => !string.IsNullOrEmpty(a?.PicName))
                                .DistinctBy(a => a.SeiyuuID)
                                .ToList();
                            if (creators == null || creators.Count == 0)
                            {
                                logger.Warn(
                                    $"AniDB Seiyuu image failed to download: Can't find Seiyuus for anime: {AnimeID}");
                                return;
                            }

                            foreach (var creator in creators)
                            {
                                downloadURLs.Add(string.Format(Constants.URLS.AniDB_Images, creator.PicName));
                                fileNames.Add(creator.GetPosterPath());
                            }

                            ShokoService.CmdProcessorGeneral.QueueState = PrettyDescriptionCreators;
                            break;
                    }

                    if (downloadURLs.Count == 0 || fileNames.All(a => string.IsNullOrEmpty(a)))
                    {
                        logger.Warn("Image failed to download: No URLs were generated. This should never happen");
                        return;
                    }


                    for (int i = 0; i < downloadURLs.Count; i++)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(fileNames[i])) continue;
                            bool downloadImage = true;
                            bool fileExists = File.Exists(fileNames[i]);
                            bool imageValid = fileExists && Misc.IsImageValid(fileNames[i]);

                            if (imageValid && !ForceDownload) downloadImage = false;

                            if (!downloadImage) continue;

                            string tempName = Path.Combine(ImageUtils.GetImagesTempFolder(),
                                Path.GetFileName(fileNames[i]));

                            try
                            {
                                if (fileExists) File.Delete(fileNames[i]);
                            }
                            catch (Exception ex)
                            {
                                Thread.CurrentThread.CurrentUICulture =
                                    CultureInfo.GetCultureInfo(ServerSettings.Instance.Culture);

                                logger.Warn(Resources.Command_DeleteError, fileNames, ex.Message);
                                return;
                            }

                            // If this has any issues, it will throw an exception, so the catch below will handle it
                            RecursivelyRetryDownload(downloadURLs[i], ref tempName, 0, 5);

                            // move the file to it's final location
                            // check that the final folder exists
                            string fullPath = Path.GetDirectoryName(fileNames[i]);
                            if (!Directory.Exists(fullPath))
                                Directory.CreateDirectory(fullPath);

                            File.Move(tempName, fileNames[i]);
                            logger.Info($"Image downloaded: {fileNames[i]} from {downloadURLs[i]}");
                        }
                        catch (WebException e)
                        {
                            logger.Warn("Error processing CommandRequest_DownloadAniDBImages: {0} ({1}) - {2}",
                                downloadURLs[i],
                                AnimeID,
                                e.Message);
                        }catch (Exception e)
                        {
                            logger.Error("Error processing CommandRequest_DownloadAniDBImages: {0} ({1}) - {2}",
                                downloadURLs[i],
                                AnimeID,
                                e);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error("Error processing CommandRequest_DownloadAniDBImages: {0} - {1}", AnimeID, ex);
            }
            AniDbRateLimiter.Instance.Reset();
        }

        private void RecursivelyRetryDownload(string downloadURL, ref string tempFilePath, int count, int maxretry)
        {
            try
            {
                // download image
                if (downloadURL.Length <= 0) return;

                // Ignore all certificate failures.
                ServicePointManager.Expect100Continue = true;                
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("user-agent", "JMM");
                    //OnImageDownloadEvent(new ImageDownloadEventArgs("", req, ImageDownloadEventType.Started));
                    //BaseConfig.MyAnimeLog.Write("ProcessImages: Download: {0}  *** to ***  {1}", req.URL, fullName);

                    AniDbImageRateLimiter.Instance.EnsureRate();
                    byte[] bytes = client.DownloadData(downloadURL);
                    AniDbImageRateLimiter.Instance.Reset();
                    if (bytes.Length < 4)
                        throw new WebException(
                            "The image download stream returned less than 4 bytes (a valid image has 2-4 bytes in the header)");

                    ImageFormatEnum imageFormat = Misc.GetImageFormat(bytes);
                    string extension;
                    switch (imageFormat)
                    {
                        case ImageFormatEnum.bmp:
                            extension = ".bmp";
                            break;
                        case ImageFormatEnum.gif:
                            extension = ".gif";
                            break;
                        case ImageFormatEnum.jpeg:
                            extension = ".jpeg";
                            break;
                        case ImageFormatEnum.png:
                            extension = ".png";
                            break;
                        case ImageFormatEnum.tiff:
                            extension = ".tiff";
                            break;
                        default: throw new WebException("The image download stream returned an invalid image");
                    }

                    if (extension.Length <= 0) return;
                    string newFile = Path.ChangeExtension(tempFilePath, extension);
                    if(newFile == null) return;

                    if (File.Exists(newFile)) File.Delete(newFile);
                    using (var fs = new FileStream(newFile, FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    tempFilePath = newFile;
                }
            }
            catch (WebException)
            {
                if (count + 1 >= maxretry) throw;
                Thread.Sleep(500);
                RecursivelyRetryDownload(downloadURL, ref tempFilePath, count + 1, maxretry);
            }
        }

        private string GetFileName(ImageDownloadRequest req, bool thumbNailOnly)
        {
            switch (req.ImageType)
            {
                case ImageEntityType.AniDB_Cover:
                    SVR_AniDB_Anime anime = req.ImageData as SVR_AniDB_Anime;
                    return anime.PosterPath;

                case ImageEntityType.AniDB_Character:
                    AniDB_Character chr = req.ImageData as AniDB_Character;
                    return chr.GetPosterPath();

                case ImageEntityType.AniDB_Creator:
                    AniDB_Seiyuu creator = req.ImageData as AniDB_Seiyuu;
                    return creator.GetPosterPath();

                default:
                    return string.Empty;
            }
        }

        public override void GenerateCommandID()
        {
            CommandID = $"CommandRequest_DownloadImage_{AnimeID}_{ForceDownload}";
        }

        public override bool LoadFromDBCommand(CommandRequest cq)
        {
            CommandID = cq.CommandID;
            CommandRequestID = cq.CommandRequestID;
            Priority = cq.Priority;
            CommandDetails = cq.CommandDetails;
            DateTimeUpdated = cq.DateTimeUpdated;

            // read xml to get parameters
            if (CommandDetails.Trim().Length > 0)
            {
                XmlDocument docCreator = new XmlDocument();
                docCreator.LoadXml(CommandDetails);

                // populate the fields
                AnimeID = int.Parse(TryGetProperty(docCreator, "CommandRequest_DownloadAniDBImages", "AnimeID"));
                ForceDownload =
                    bool.Parse(TryGetProperty(docCreator, "CommandRequest_DownloadAniDBImages", "ForceDownload"));
            }

            return true;
        }

        public override CommandRequest ToDatabaseObject()
        {
            GenerateCommandID();

            CommandRequest cq = new CommandRequest
            {
                CommandID = CommandID,
                CommandType = CommandType,
                Priority = Priority,
                CommandDetails = ToXML(),
                DateTimeUpdated = DateTime.Now
            };
            return cq;
        }
    }
}
