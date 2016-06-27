using DotCMIS.Client;
using DotCMIS.Exceptions;
using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using DotCMIS.Client.Impl;
using CmisSync.Lib.Cmis;


namespace CmisSync.Lib.Sync
{
    /// <summary>
    /// Part of CmisRepo.
    /// </summary>
    public partial class CmisRepo : RepoBase
    {
        /// <summary>
        /// Synchronization with a particular CMIS folder.
        /// </summary>
        public partial class SynchronizedFolder
        {
            /// <summary>
            /// Synchronize by checking all folders/files one-by-one.
            /// This strategy is used if the CMIS server does not support the ChangeLog feature.
            /// 
            /// for all remote folders:
            ///     if exists locally:
            ///       recurse
            ///     else
            ///       if in database:
            ///         delete recursively from server // if BIDIRECTIONAL
            ///       else
            ///         download recursively
            /// for all remote files:
            ///     if exists locally:
            ///       if remote is more recent than local:
            ///         download
            ///       else
            ///         upload                         // if BIDIRECTIONAL
            ///     else:
            ///       if in database:
            ///         delete from server             // if BIDIRECTIONAL
            ///       else
            ///         download
            /// for all local files:
            ///   if not present remotely:
            ///     if in database:
            ///       delete
            ///     else:
            ///       upload                           // if BIDIRECTIONAL
            ///   else:
            ///     if has changed locally:
            ///       upload                           // if BIDIRECTIONAL
            /// for all local folders:
            ///   if not present remotely:
            ///     if in database:
            ///       delete recursively from local
            ///     else:
            ///       upload recursively               // if BIDIRECTIONAL
            /// </summary>
            /// <returns>
            /// True if all content has been successfully synchronized.
            /// False if anything has failed or been skipped.
            /// </returns>
            private bool CrawlSync(IFolder remoteFolder, string remotePath, string localFolder)
            {
                SleepWhileSuspended();

                /*if (IsGetDescendantsSupported)  Disabled because it causes server-side problems for folders with a huge number of files.
                {
                    IList<ITree<IFileableCmisObject>> desc;
                    try
                    {
                        desc = remoteFolder.GetDescendants(-1);
                    }
                    catch (DotCMIS.Exceptions.CmisConnectionException ex)
                    {
                        if (ex.InnerException is System.Xml.XmlException)
                        {
                            Logger.Warn(String.Format("CMIS::getDescendants() response could not be parsed: {0}", ex.InnerException.Message));
                        }
                        throw;
                    }
                    CrawlDescendants(remoteFolder, desc, localFolder);
                }*/

                // Lists of files/folders, to delete those that have been removed on the server.
                IList<string> remoteFiles = new List<string>();
                IList<string> remoteSubfolders = new List<string>();

                // Crawl remote children.
                // Logger.LogInfo("Sync", String.Format("Crawl remote folder {0}", this.remoteFolderPath));
                bool success = CrawlRemote(remoteFolder, remotePath, localFolder, remoteFiles, remoteSubfolders);

                // Crawl local files.
                // Logger.LogInfo("Sync", String.Format("Crawl local files in the local folder {0}", localFolder));
                CrawlLocalFiles(localFolder, remoteFolder, remoteFiles);

                // Crawl local folders.
                // Logger.LogInfo("Sync", String.Format("Crawl local folder {0}", localFolder));
                CrawlLocalFolders(localFolder, remoteFolder, remoteSubfolders);

                return success;
            }


            private void CrawlSyncAndUpdateChangeLogToken(IFolder remoteFolder, string remotePath, string localFolder)
            {
                // Get ChangeLog token.
                string token = CmisUtils.GetChangeLogToken(session);

                // Sync.
                bool success = CrawlSync(remoteFolder, remotePath, localFolder);

                // Update ChangeLog token if sync has been successful.
                if (success)
                {
                    database.SetChangeLogToken(token);
                }
                else
                {
                    Logger.Info("ChangeLog token not updated as an error occurred during sync.");
                }
            }


            /// <summary>
            /// Crawl remote content, syncing down if needed.
            /// Meanwhile, cache remoteFiles and remoteFolders, they are output parameters that are used in CrawlLocalFiles/CrawlLocalFolders
            /// </summary>
            private bool CrawlRemote(IFolder remoteFolder, string remotePath, string localFolder, IList<string> remoteFiles, IList<string> remoteFolders)
            {
                bool success = true;
                SleepWhileSuspended();

                // Get all remote children.
                // TODO: use paging
                IOperationContext operationContext = session.CreateOperationContext();
                operationContext.MaxItemsPerPage = Int32.MaxValue;
                foreach (ICmisObject cmisObject in remoteFolder.GetChildren(operationContext))
                {
                    try
                    {
                        if (cmisObject is DotCMIS.Client.Impl.Folder)
                        {
                            // It is a CMIS folder.
                            IFolder remoteSubFolder = (IFolder)cmisObject;
                            string remoteSubPath = remotePath + CmisUtils.CMIS_FILE_SEPARATOR + remoteSubFolder.Name;
                            CrawlRemoteFolder(remoteSubFolder, remoteSubPath, localFolder, remoteFolders);
                        }
                        else if (cmisObject is DotCMIS.Client.Impl.Document)
                        {
                            // It is a CMIS document.
                            IDocument remoteDocument = (IDocument)cmisObject;
                            string remoteDocumentPath = remotePath + CmisUtils.CMIS_FILE_SEPARATOR + remoteDocument.Name;
                            CrawlRemoteDocument(remoteDocument, remoteDocumentPath, localFolder, remoteFiles);
                        }
                        else if (isLink(cmisObject))
                        {
                            Logger.Debug("Ignoring file '" + remoteFolder + "/" + cmisObject.Name + "' of type '" +
                                cmisObject.ObjectType.Description + "'. Links are not currently handled.");
                        }
                        else
                        {
                            Logger.Warn("Unknown object type: '" + cmisObject.ObjectType.Description + "' (" + cmisObject.ObjectType.DisplayName
                                + ") for object " + remoteFolder + "/" + cmisObject.Name);
                        }
                    }
                    catch (CmisBaseException e)
                    {
                        ProcessRecoverableException("Could not access remote object: " + cmisObject.Name, e);
                        success = false;
                    }
                }
                return success;
            }

            private bool isLink(ICmisObject cmisObject)
            {
                IObjectType parent = cmisObject.ObjectType.GetParentType();
                while (parent != null)
                {
                    if(parent.Id.Equals("I:cm:link")){
                    return true;
                    }
                    parent = parent.GetParentType();
                }
                return false;
            }

            /// <summary>
            /// Crawl remote subfolder, syncing down if needed.
            /// Meanwhile, cache all contained remote folders, they are output parameters that are used in CrawlLocalFiles/CrawlLocalFolders
            /// </summary>
            private void CrawlRemoteFolder(IFolder remoteSubFolder, string remotePath, string localFolder, IList<string> remoteFolders)
            {
                SleepWhileSuspended();

                try
                {
                    if (Utils.WorthSyncing(localFolder, remoteSubFolder.Name, repoInfo))
                    {
                        // Logger.Debug("CrawlRemote localFolder:\"" + localFolder + "\" remoteSubFolder.Path:\"" + remoteSubFolder.Path + "\" remoteSubFolder.Name:\"" + remoteSubFolder.Name + "\"");
                        remoteFolders.Add(remoteSubFolder.Name);
                        var subFolderItem = database.GetFolderSyncItemFromRemotePath(remoteSubFolder.Path);
                        if (null == subFolderItem)
                        {
                            subFolderItem = SyncItemFactory.CreateFromRemoteFolder(remoteSubFolder.Path, repoInfo, database);
                        }

                        // Check whether local folder exists.
                        if (Directory.Exists(subFolderItem.LocalPath))
                        {
                            // Recurse into folder.
                            CrawlSync(remoteSubFolder, remotePath, subFolderItem.LocalPath);
                        }
                        else
                        {
                            // Maybe the whole synchronized folder has disappeared?
                            // While rare for normal filesystems, that happens rather often with mounted folders (for instance encrypted folders)
                            // In such a case, we should abort this synchronization rather than delete the remote subfolder.
                            if (!Directory.Exists(repoInfo.TargetDirectory))
                            {
                                throw new Exception("Local folder has disappeared: " + repoInfo.TargetDirectory + " , aborting synchronization");
                            }

                            // If there was previously a file with this name, delete it.
                            // TODO warn if local changes in the file.
                            if (File.Exists(subFolderItem.LocalPath))
                            {
                                activityListener.ActivityStarted();
                                Utils.DeleteEvenIfReadOnly(subFolderItem.LocalPath);
                                activityListener.ActivityStopped();
                            }

                            if (database.ContainsFolder(subFolderItem))
                            {
                                // If there was previously a folder with this name, it means that
                                // the user has deleted it voluntarily, so delete it from server too.

                                activityListener.ActivityStarted();

                                // Delete the folder from the remote server.
                                try
                                {
                                    Logger.Debug("Removing remote folder tree: " + remoteSubFolder.Path);
                                    IList<string> failedIDs = remoteSubFolder.DeleteTree(true, null, true);
                                    if (failedIDs == null || failedIDs.Count != 0)
                                    {
                                        Logger.Error("Failed to completely delete remote folder " + remoteSubFolder.Path);
                                        // TODO Should we retry? Maybe at least once, as a manual recursion instead of a DeleteTree.
                                    }
                                }
                                catch (CmisPermissionDeniedException e)
                                {
                                    // We don't have the permission to delete this folder. Warn and recreate it.
                                    Utils.NotifyUser("You don't have the necessary permissions to delete folder " + remoteSubFolder.Path
                                        + "\nIf you feel you should be able to delete it, please contact your server administrator");
                                    RecursiveFolderCopy(remoteSubFolder, remotePath, subFolderItem.LocalPath);
                                }

                                // Delete the folder from database.
                                database.RemoveFolder(subFolderItem);

                                activityListener.ActivityStopped();
                            }
                            else
                            {
                                // The folder has been recently created on server, so download it.
                                activityListener.ActivityStarted();
                                Directory.CreateDirectory(subFolderItem.LocalPath);

                                // Create database entry for this folder.
                                // TODO - Yannick - Add metadata
                                database.AddFolder(subFolderItem, remoteSubFolder.Id, remoteSubFolder.LastModificationDate);
                                Logger.Info("Added folder to database: " + subFolderItem.LocalPath);

                                // Recursive copy of the whole folder.
                                RecursiveFolderCopy(remoteSubFolder, remotePath, subFolderItem.LocalPath);

                                activityListener.ActivityStopped();
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    activityListener.ActivityStopped();
                    ProcessRecoverableException("Could not crawl sync remote folder: " + remoteSubFolder.Path, e);
                }
            }

            /// <summary>
            /// Crawl remote document, syncing down if needed.
            /// Meanwhile, cache remoteFiles, they are output parameters that are used in CrawlLocalFiles/CrawlLocalFolders
            /// </summary>
            private void CrawlRemoteDocument(IDocument remoteDocument, string remotePath, string localFolder, IList<string> remoteFiles)
            {
                SleepWhileSuspended();

                if (Utils.WorthSyncing(localFolder, repoInfo.CmisProfile.localFilename(remoteDocument), repoInfo))
                {
                    // We use the filename of the document's content stream.
                    // This can be different from the name of the document.
                    // For instance in FileNet it is not unusual to have a document where
                    // document.Name is "foo" and document.ContentStreamFileName is "foo.jpg".
                    string remoteDocumentFileName = repoInfo.CmisProfile.localFilename(remoteDocument);
                    //Logger.Debug("CrawlRemote doc: " + localFolder + CmisUtils.CMIS_FILE_SEPARATOR + remoteDocumentFileName);

                    // If this file does not have a filename, ignore it.
                    // It sometimes happen on IBM P8 CMIS server, not sure why.
                    if (remoteDocumentFileName == null)
                    {
                        Logger.Warn("Skipping download of '" + repoInfo.CmisProfile.localFilename(remoteDocument) + "' with null content stream in " + localFolder);
                        return;
                    }

                    remoteFiles.Add(remoteDocumentFileName);

                    var paths = remoteDocument.Paths;
                    var pathsCount = paths.Count;
                    var syncItem = database.GetSyncItemFromRemotePath(remotePath);
                    if (null == syncItem)
                    {
                        syncItem = SyncItemFactory.CreateFromRemoteDocument(remotePath, repoInfo.CmisProfile.localFilename(remoteDocument), repoInfo, database);
                    }

                    if (syncItem.FileExistsLocal())
                    {
                        // Check modification date stored in database and download if remote modification date if different.
                        DateTime? serverSideModificationDate = ((DateTime)remoteDocument.LastModificationDate).ToUniversalTime();
                        DateTime? lastDatabaseUpdate = database.GetServerSideModificationDate(syncItem);

                        if (lastDatabaseUpdate == null)
                        {
                            Logger.Info("Downloading file absent from database: " + syncItem.LocalPath);
                            activityListener.ActivityStarted();
                            DownloadFile(remoteDocument, remotePath, localFolder);
                            activityListener.ActivityStopped();
                        }
                        else
                        {
                            // If the file has been modified since last time we downloaded it, then download again.
                            if (serverSideModificationDate > lastDatabaseUpdate)
                            {
                                activityListener.ActivityStarted();

                                if (database.LocalFileHasChanged(syncItem.LocalPath))
                                {
                                    Logger.Info("Conflict with file: " + remoteDocumentFileName + ", backing up locally modified version and downloading server version");
                                    Logger.Info("- serverSideModificationDate: " + serverSideModificationDate);
                                    Logger.Info("- lastDatabaseUpdate: " + lastDatabaseUpdate);
                                    Logger.Info("- Checksum in database: " + database.GetChecksum(syncItem.LocalPath));
                                    Logger.Info("- Checksum of local file: " + Database.Database.Checksum(syncItem.LocalPath));

                                    // Rename locally modified file.
                                    String newFilePath = Utils.CreateConflictFilename(syncItem.LocalPath, repoInfo.User);
                                    File.Move(syncItem.LocalPath, newFilePath);

                                    // Download server version
                                    DownloadFile(remoteDocument, remotePath, localFolder);
                                    Logger.Info("- Checksum of remote file: " + Database.Database.Checksum(syncItem.LocalPath));
                                    repo.OnConflictResolved();

                                    // Notify the user.
                                    string lastModifiedBy = CmisUtils.GetProperty(remoteDocument, "cmis:lastModifiedBy");
                                    string message = String.Format(
                                        // Properties_Resources.ResourceManager.GetString("ModifiedSame", CultureInfo.CurrentCulture),
                                        "User {0} modified file \"{1}\" at the same time as you.",
                                        lastModifiedBy, syncItem.LocalPath)
                                        + "\n\n"
                                        // + Properties_Resources.ResourceManager.GetString("YourVersion", CultureInfo.CurrentCulture);
                                        + "Your version has been saved as \"" + newFilePath + "\", please merge your important changes from it and then delete it.";
                                    Logger.Info(message);
                                    Utils.NotifyUser(message);
                                }
                                else
                                {
                                    Logger.Info("Downloading modified file: " + remoteDocumentFileName);
                                    DownloadFile(remoteDocument, remotePath, localFolder);
                                }

                                activityListener.ActivityStopped();
                            }
                        }
                    }
                    else
                    {
                        // The remote file does not exist on the local filesystem.

                        // Maybe the whole synchronized folder has disappeared?
                        // While rare for normal filesystems, that happens rather often with mounted folders (for instance encrypted folders)
                        // In such a case, we should abort this synchronization rather than delete the remote file.
                        if ( ! Directory.Exists(repoInfo.TargetDirectory))
                        {
                            throw new Exception("Local folder has disappeared: " + repoInfo.TargetDirectory + " , aborting synchronization");
                        }

                        if (database.ContainsLocalFile(syncItem.LocalRelativePath))
                        {
                            // The file used to be present locally (as revealed by the database), but does not exist anymore locally.
                            // So, it must have been deleted locally by the user.
                            // Thus, CmisSync must remove the file from the server too.

                            string message0 = "CmisSync Warning: You have deleted file " + syncItem.LocalPath +
                                "\nCmisSync will now delete it from the server. If you actually did not delete this file, please report a bug at CmisSync@aegif.jp";
                            Logger.Info(message0);
                            //Utils.NotifyUser(message0);

                            if ((bool)remoteDocument.IsVersionSeriesCheckedOut
                                && ! remoteDocument.VersionSeriesCheckedOutBy.Equals(repoInfo.User))
                            {
                                string message = String.Format("Restoring file \"{0}\" because it is checked out on the server by another user: {1}",
                                    syncItem.LocalPath, remoteDocument.VersionSeriesCheckedOutBy);
                                Logger.Info(message);
                                Utils.NotifyUser(message);

                                // Restore the deleted file
                                activityListener.ActivityStarted();
                                DownloadFile(remoteDocument, remotePath, localFolder);
                                activityListener.ActivityStopped();
                            }
                            else
                            {
                                // File has been recently removed locally, so remove it from server too.

                                activityListener.ActivityStarted();
                                Logger.Info("Removing locally deleted file on server: " + syncItem.RemotePath);
                                remoteDocument.DeleteAllVersions();
                                // Remove it from database.
                                database.RemoveFile(syncItem);
                                activityListener.ActivityStopped();
                            }
                        }
                        else
                        {
                            // New remote file, download it.

                            Logger.Info("New remote file: " + syncItem.RemotePath);
                            activityListener.ActivityStarted();
                            DownloadFile(remoteDocument, remotePath, localFolder);
                            activityListener.ActivityStopped();
                        }
                    }
                }
            }


            /// <summary>
            /// Crawl local files in a given directory (not recursive).
            /// </summary>
            private void CrawlLocalFiles(string localFolder, IFolder remoteFolder, IList<string> remoteFiles)
            {
                SleepWhileSuspended();

                string[] files;
                try
                {
                    files = Directory.GetFiles(localFolder);
                }
                catch (Exception e)
                {
                    Logger.Warn("Could not get the file list from folder: " + localFolder, e);
                    return;
                }

                foreach (string filePath in files)
                {
                    CrawlLocalFile(filePath, remoteFolder, remoteFiles);
                }
            }

            /// <summary>
            /// Crawl local file in a given directory (not recursive).
            /// </summary>
            private void CrawlLocalFile(string filePath, IFolder remoteFolder, IList<string> remoteFiles)
            {
                SleepWhileSuspended();

                try
                {
                    if (Utils.IsSymlink(new FileInfo(filePath)))
                    {
                        Logger.Info("Skipping symbolic linked file: " + filePath);
                        return;
                    }
                    
                    var item = database.GetSyncItemFromLocalPath(filePath);
                    if (null == item)
                    {
                        // The file has been recently created locally (not synced from server).
                        item = SyncItemFactory.CreateFromLocalPath(filePath, false, repoInfo, database);
                    }

                    // string fileName = Path.GetFileName(filePath);
                    string fileName = item.RemoteLeafname;

                    if (Utils.WorthSyncing(Path.GetDirectoryName(filePath), fileName, repoInfo))
                    {
                        if (!(remoteFiles.Contains(fileName) ||
                            // Workaround for Documentum which sometimes put a ".zip" extension to document names.
                            (CmisUtils.IsDocumentum(session) && remoteFiles.Contains(fileName + ".zip"))))
                        {
                            // This local file is not on the CMIS server now, so
                            // check whether it used to exist on server or not.
                            if (database.ContainsLocalFile(filePath))
                            {
                                if (database.LocalFileHasChanged(filePath))
                                {
                                    // If file has changed locally, move to 'your_version' and warn about conflict
                                    if (BIDIRECTIONAL)
                                    {
                                        // Local file was updated, sync up.
                                        Logger.Info("Uploading locally edited remotely removed file from the repository: " + filePath);
                                        activityListener.ActivityStarted();
                                        UploadFile(filePath, remoteFolder);
                                        activityListener.ActivityStopped();
                                    }
                                    else
                                    {
                                        Logger.Info("Conflict with file: " + filePath + ", backing up locally modified version.");
                                        activityListener.ActivityStarted();
                                        // Rename locally modified file.
                                        String newFilePath = Utils.CreateConflictFilename(filePath, repoInfo.User);

                                        // The file might be ReadOnly, so make it writable first, otherwise the move will fail.
                                        File.SetAttributes(filePath, FileAttributes.Normal); // TODO use Utils.DeleteEvenIfReadOnly

                                        File.Move(filePath, newFilePath);

                                        // Delete file from database.
                                        database.RemoveFile(item);

                                        repo.OnConflictResolved();
                                        activityListener.ActivityStopped();
                                    }
                                }
                                else
                                {
                                    // File has been deleted on server, so delete it locally.
                                    Logger.Info("Removing remotely deleted file: " + filePath);
                                    activityListener.ActivityStarted();

                                    // The file might be ReadOnly, so make it writable first, otherwise removal will fail.
                                    File.SetAttributes(filePath, FileAttributes.Normal); // TODO use Utils.DeleteEvenIfReadOnly

                                    // Delete from the local filesystem.
                                    File.Delete(filePath);

                                    // Delete file from database.
                                    database.RemoveFile(item);

                                    activityListener.ActivityStopped();
                                }
                            }
                            else
                            {
                                if (BIDIRECTIONAL)
                                {
                                    // New file, sync up.
                                    Logger.Info("Uploading file absent on repository: " + filePath);
                                    activityListener.ActivityStarted();
                                    UploadFile(filePath, remoteFolder);
                                    activityListener.ActivityStopped();
                                }
                            }
                        }
                        else
                        {
                            // The file exists both on server and locally.
                            if (database.LocalFileHasChanged(filePath))
                            {
                                if (BIDIRECTIONAL)
                                {
                                    // Upload new version of file content.
                                    Logger.Info("Uploading file update on repository: " + filePath);
                                    activityListener.ActivityStarted();
                                    UpdateFile(filePath, remoteFolder);
                                    activityListener.ActivityStopped();
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    ProcessRecoverableException("Could not crawl sync local file: " + filePath, e);
                }
            }


            /// <summary>
            /// Crawl local folders in a given directory (not recursive).
            /// </summary>
            private void CrawlLocalFolders(string localFolder, IFolder remoteFolder, IList<string> remoteFolders)
            {
                SleepWhileSuspended();

                string[] folders;
                try
                {
                    folders = Directory.GetDirectories(localFolder);
                }
                catch (Exception e)
                {
                    Logger.Warn(String.Format("Exception while get the folder list from folder {0}", localFolder), e);
                    return;
                }

                foreach (string localSubFolder in folders)
                {
                    CrawlLocalFolder(localSubFolder, remoteFolder, remoteFolders);
                }
            }

            /// <summary>
            /// Crawl local folder in a given directory (not recursive).
            /// </summary>
            private void CrawlLocalFolder(string localSubFolder, IFolder remoteFolder, IList<string> remoteFolders)
            {
                SleepWhileSuspended();
                try
                {
                    if (Utils.IsSymlink(new DirectoryInfo(localSubFolder)))
                    {
                        Logger.Info("Skipping symbolic link folder: " + localSubFolder);
                        return;
                    }

                    string folderName = Path.GetFileName(localSubFolder);
                    var syncFolderItem = database.GetFolderSyncItemFromLocalPath(localSubFolder);
                    if (null == syncFolderItem)
                    {
                        syncFolderItem = SyncItemFactory.CreateFromLocalPath(localSubFolder, true, repoInfo, database);
                    }

                    if (Utils.WorthSyncing(Path.GetDirectoryName(localSubFolder), folderName, repoInfo))
                    {
                        if (!remoteFolders.Contains(syncFolderItem.RemoteLeafname))
                        {
                            // This local folder is not on the CMIS server now, so
                            // check whether it used to exist on server or not.
                            if (database.ContainsFolder(syncFolderItem))
                            {
                                activityListener.ActivityStarted();
                                RemoveFolderLocally(localSubFolder);
                                activityListener.ActivityStopped();
                            }
                            else
                            {
                                if (BIDIRECTIONAL)
                                {
                                    // New local folder, upload recursively.
                                    activityListener.ActivityStarted();
                                    UploadFolderRecursively(remoteFolder, localSubFolder);
                                    activityListener.ActivityStopped();
                                }
                            }
                        }
                    }

                }
                catch (Exception e)
                {
                    ProcessRecoverableException("Could not crawl sync local folder: " + localSubFolder, e);
                }
            }
        }
    }
}
