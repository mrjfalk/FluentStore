﻿using Microsoft.Toolkit.Uwp.Notifications;
using MicrosoftStore.Models;
using StoreLib.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Management.Deployment;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Notifications;

namespace FluentStore.Helpers
{
    public static class PackageHelper
    {
        public static readonly string[] INSTALLABLE_EXTS = new string[]
        {
            ".appx", ".appxbundle", ".msix", ".msixbundle"
        };

        public static async Task<bool> InstallPackage(PackageInstance package, ProductDetails product, bool? useAppInstaller = null)
        {
            ToastNotification finalNotif = GenerateInstallSuccessToast(package, product);
            bool isSuccess = true;
            try
            {
                (await DownloadPackage(package, product, false)).Deconstruct(out var installer, out var progressToast);

                PackageManager pkgManager = new PackageManager();
                Progress<DeploymentProgress> progressCallback = new Progress<DeploymentProgress>(prog =>
                {
                    ToastNotificationManager.GetDefault().CreateToastNotifier().Update(
                        new NotificationData(new Dictionary<string, string>()
                        {
                            { "progressValue", (prog.percentage / 100).ToString() },
                            { "progressStatus", "Installing..." }
                        }),
                        progressToast.Tag
                    );
                });

                if (Settings.Default.UseAppInstaller || (useAppInstaller.HasValue && useAppInstaller.Value))
                {
                    // Pass the file to App Installer to install it
                    Uri launchUri = new Uri("ms-appinstaller:?source=" + installer.Path);
                    switch (await Launcher.QueryUriSupportAsync(launchUri, LaunchQuerySupportType.Uri))
                    {
                        case LaunchQuerySupportStatus.Available:
                            // TODO: Somehow this is trying to install a .NET Framework installer. :/
                            isSuccess = await Launcher.LaunchUriAsync(launchUri);
                            if (!isSuccess)
                                finalNotif = GenerateInstallFailureToast(package, product, new Exception("Failed to launch App Installer."));
                            break;

                        case LaunchQuerySupportStatus.AppNotInstalled:
                            finalNotif = GenerateInstallFailureToast(package, product, new Exception("App Installer is not available on this device."));
                            isSuccess = false;
                            break;

                        case LaunchQuerySupportStatus.AppUnavailable:
                            finalNotif = GenerateInstallFailureToast(package, product, new Exception("App Installer is not available right now, try again later."));
                            isSuccess = false;
                            break;

                        case LaunchQuerySupportStatus.Unknown:
                        default:
                            finalNotif = GenerateInstallFailureToast(package, product, new Exception("An unknown error occured."));
                            isSuccess = false;
                            break;
                    }
                }
                else
                {
                    // Attempt to install the downloaded package
                    var result = await pkgManager.AddPackageByUriAsync(
                        new Uri(installer.Path),
                        new AddPackageOptions()
                        {
                            ForceAppShutdown = true
                        }
                    ).AsTask(progressCallback);

                    if (result.IsRegistered)
                        finalNotif = GenerateInstallSuccessToast(package, product);
                    else
                        finalNotif = GenerateInstallFailureToast(package, product, result.ExtendedErrorCode);
                    isSuccess = result.IsRegistered;
                }

                // Hide progress notification
                ToastNotificationManager.GetDefault().CreateToastNotifier().Hide(progressToast);
                // Show the final notification
                ToastNotificationManager.GetDefault().CreateToastNotifier().Show(finalNotif);

                await installer.DeleteAsync();

                return true;
            }
            catch (Exception ex)
            {
                ToastNotificationManager.GetDefault().CreateToastNotifier().Show(GenerateInstallFailureToast(package, product, ex));
                return false;
            }
        }

        public static async Task<Tuple<StorageFile, ToastNotification>> DownloadPackage(PackageInstance package, ProductDetails product, bool hideProgressToastWhenDone = true)
        {
            // Download the file to the app's temp directory
            //var client = new System.Net.WebClient();
            string filepath = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, package.PackageMoniker);
            Debug.WriteLine(filepath);
            //client.DownloadFile(package.Uri, filepath);

            StorageFile destinationFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                package.PackageMoniker, CreationCollisionOption.ReplaceExisting);
            BackgroundDownloader downloader = new BackgroundDownloader();
            downloader.FailureToastNotification = GenerateDownloadFailureToast(package, product);
            var progressToast = GenerateProgressToast(package, product);
            Debug.WriteLine(package.PackageUri.AbsoluteUri);
            DownloadOperation download = downloader.CreateDownload(package.PackageUri, destinationFile);
            download.RangesDownloaded += (op, args) =>
            {
                ToastNotificationManager.GetDefault().CreateToastNotifier().Update(
                    new NotificationData(new Dictionary<string, string>()
                    {
                            { "progressValue", ((double)op.Progress.BytesReceived / op.Progress.TotalBytesToReceive).ToString() },
                            { "progressStatus", "Downloading..." }
                    }),
                    progressToast.Tag
                );
            };
            ToastNotificationManager.GetDefault().CreateToastNotifier().Show(progressToast);
            await download.StartAsync();
            if (hideProgressToastWhenDone)
                ToastNotificationManager.GetDefault().CreateToastNotifier().Hide(progressToast);

            string extension = "";
            string contentTypeFilepath = filepath + "_[Content_Types].xml";
            using (var archive = ZipFile.OpenRead(filepath))
            {
                var entry = archive.GetEntry("[Content_Types].xml");
                entry.ExtractToFile(contentTypeFilepath, true);
                var ctypesXml = XDocument.Load(contentTypeFilepath);
                var defaults = ctypesXml.Root.Elements().Where(e => e.Name.LocalName == "Default");
                if (defaults.Any(d => d.Attribute("Extension").Value == "msix"))
                {
                    // Package contains one or more MSIX packages
                    extension += ".msix";
                }
                else if (defaults.Any(d => d.Attribute("Extension").Value == "appx"))
                {
                    // Package contains one or more MSIX packages
                    extension += ".appx";
                }
                if (defaults.Any(defaults => defaults.Attribute("ContentType").Value == "application/vnd.ms-appx.bundlemanifest+xml"))
                {
                    // Package is a bundle
                    extension += "bundle";
                }
            }
            if (File.Exists(contentTypeFilepath))
                File.Delete(contentTypeFilepath);

            if (extension != "")
                await destinationFile.RenameAsync(destinationFile.Name + extension, NameCollisionOption.ReplaceExisting);

            return (destinationFile, progressToast).ToTuple();
        }

        public static PackageInstance GetLatestDesktopPackage(List<PackageInstance> packages, string family, ProductDetails product)
        {
            List<PackageInstance> installables = packages.FindAll(p => p.Version.Revision != 70);
            if (installables.Count <= 0)
                return null;
            // TODO: Add addtional checks that might take longer that the user can enable 
            // if they are having issues
            return installables.OrderByDescending(p => p.Version).First();
        }

        public static List<Windows.ApplicationModel.Package> GetInstalledPackages()
        {
            // TOOD: Always throws "access denied"
            PackageManager pkgManager = new PackageManager();
            return pkgManager.FindPackages().ToList();
        }
        
        public static bool IsFiletype(string file, params string[] exts)
        {
            foreach (string ext in exts)
            {
                if (Path.GetExtension(file) == ext)
                    return true;
            }
            return false;
        }

        private static readonly Uri dummyUri = new Uri("mailto:dummy@seznam.cz");
        /// <summary>
        /// Check if target <paramref name="packageName"/> is installed on this device.
        /// </summary>
        /// <param name="packageName">Package name in format: "949FFEAB.Email.cz_refxrrjvvv3cw"</param>
        /// <returns>True is app is installed on this device, false otherwise.</returns>
        public static async Task<bool> IsAppInstalledAsync(string packageName)
        {
            try
            {
                bool appInstalled;
                LaunchQuerySupportStatus result = await Launcher.QueryUriSupportAsync(dummyUri, LaunchQuerySupportType.Uri, packageName);
                switch (result)
                {
                    case LaunchQuerySupportStatus.Available:
                    case LaunchQuerySupportStatus.NotSupported:
                        appInstalled = true;
                        break;
                    //case LaunchQuerySupportStatus.AppNotInstalled:
                    //case LaunchQuerySupportStatus.AppUnavailable:
                    //case LaunchQuerySupportStatus.Unknown:
                    default:
                        appInstalled = false;
                        break;
                }

                Debug.WriteLine($"App {packageName}, query status: {result}, installed: {appInstalled}");
                return appInstalled;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking if app {packageName} is installed. Error: {ex}");
                return false;
            }
        }

        public static ToastNotification GenerateProgressToast(PackageInstance package, ProductDetails product)
        {
            var content = new ToastContent()
            {
                Visual = new ToastVisual()
                {
                    BindingGeneric = new ToastBindingGeneric()
                    {
                        Children =
                        {
                            new AdaptiveText()
                            {
                                Text = new BindableString("progressTitle")
                            },
                            new AdaptiveProgressBar()
                            {
                                Value = new BindableProgressBarValue("progressValue"),
                                Title = new BindableString("progressVersion"),
                                Status = new BindableString("progressStatus")
                            }
                        },
                        AppLogoOverride = new ToastGenericAppLogo()
                        {
                            Source = product.Images.FindLast(i => i.ImageType == MicrosoftStore.Enums.ImageType.Logo).Url
                        }
                    }
                },
                // TODO: Add cancel and pause functionality
                //Actions = new ToastActionsCustom()
                //{
                //    Buttons =
                //    {
                //        new ToastButton("Pause", $"action=pauseDownload&packageName={package.PackageMoniker}")
                //        {
                //            ActivationType = ToastActivationType.Background
                //        },
                //        new ToastButton("Cancel", $"action=cancelDownload&packageName={package.PackageMoniker}")
                //        {
                //            ActivationType = ToastActivationType.Background
                //        }
                //    }
                //},
                Launch = $"action=viewDownload&packageName={package.PackageMoniker}",
            };

            var notif = new ToastNotification(content.GetXml());
            notif.Data = new NotificationData(new Dictionary<string, string>()
            {
                { "progressTitle", product.Title },
                { "progressVersion", package.Version.ToString() },
                { "progressStatus", "Downloading..." }
            });
            notif.Tag = package.PackageFamily;
            //notif.Group = "App Downloads";
            return notif;
        }

        public static ToastNotification GenerateDownloadSuccessToast(PackageInstance package, ProductDetails product, StorageFile file)
        {
            var content = new ToastContentBuilder().SetToastScenario(ToastScenario.Reminder)
                .AddToastActivationInfo($"action=viewEvent&eventId={package.PackageMoniker}&installerPath={file.Path}", ToastActivationType.Foreground)
                .AddText(product.Title)
                .AddText(product.Title + " is ready to install")
                .AddAppLogoOverride(product.Images.FindLast(i => i.ImageType == MicrosoftStore.Enums.ImageType.Logo).Uri, addImageQuery: false)
                .Content;
            return new ToastNotification(content.GetXml());
        }

        public static ToastNotification GenerateDownloadFailureToast(PackageInstance package, ProductDetails product)
        {
            var content = new ToastContentBuilder().SetToastScenario(ToastScenario.Reminder)
                .AddToastActivationInfo($"action=viewEvent&eventId={package.PackageMoniker}", ToastActivationType.Foreground)
                .AddText(product.Title)
                .AddText("Failed to download, please try again later")
                .AddAppLogoOverride(product.Images.FindLast(i => i.ImageType == MicrosoftStore.Enums.ImageType.Logo).Uri, addImageQuery: false)
                .Content;
            return new ToastNotification(content.GetXml());
        }

        public static ToastNotification GenerateInstallSuccessToast(PackageInstance package, ProductDetails product)
        {
            var content = new ToastContentBuilder().SetToastScenario(ToastScenario.Reminder)
                .AddToastActivationInfo($"action=viewEvent&eventId={package.PackageMoniker}", ToastActivationType.Foreground)
                .AddText(product.Title)
                .AddText(product.Title + " just got installed.")
                .AddAppLogoOverride(product.Images.FindLast(i => i.ImageType == MicrosoftStore.Enums.ImageType.Logo).Uri, addImageQuery: false)
                .Content;
            return new ToastNotification(content.GetXml());
        }

        public static ToastNotification GenerateInstallFailureToast(PackageInstance package, ProductDetails product, Exception ex)
        {
            var content = new ToastContentBuilder().SetToastScenario(ToastScenario.Reminder)
                .AddToastActivationInfo($"action=viewEvent&eventId={package.PackageMoniker}", ToastActivationType.Foreground)
                .AddText(product.Title)
                .AddText(product.Title + " failed to install.")
                .AddText(ex.Message)
                .AddAppLogoOverride(product.Images.FindLast(i => i.ImageType == MicrosoftStore.Enums.ImageType.Logo).Uri, addImageQuery: false)
                .Content;
            return new ToastNotification(content.GetXml());
        }
    }
}
