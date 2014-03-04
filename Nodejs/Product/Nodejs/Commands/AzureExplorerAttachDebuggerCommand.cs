﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Microsoft.NodejsTools.Debugger.DebugEngine;
using Microsoft.NodejsTools.Debugger.Remote;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Web.WindowsAzure.Contracts;
using Microsoft.VisualStudio.WindowsAzure.Authentication;
using Microsoft.VisualStudioTools;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.NodejsTools.Commands {
    /// <summary>
    /// Provides the command to attach to an Azure web site selected in Server Explorer.
    /// </summary>
    internal class AzureExplorerAttachDebuggerCommand : Command {
        public AzureExplorerAttachDebuggerCommand() {
            try {
                ProbeAzureWebSiteAssembly();
            } catch (FileNotFoundException) {
                throw new NotSupportedException();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeAzureWebSiteAssembly() {
            // Try to grab the Azure website service. This will force assembly resolution for it, and throw
            // FileNotFoundException if Azure tools are not installed.
            NodejsPackage.GetGlobalService(typeof(IVsAzureWebSiteServices));
        }

        public override int CommandId {
            get { return (int)PkgCmdId.cmdidAzureExplorerAttachNodejsDebugger; }
        }

        public override EventHandler BeforeQueryStatus {
            get {
                return (sender, args) => {
                    var oleMenuCmd = (OleMenuCommand)sender;
                    oleMenuCmd.Supported = oleMenuCmd.Visible = (GetSelectedAzureWebSite() != null);
                };
            }
        }

        public override void DoCommand(object sender, EventArgs args) {
            var webSite = GetSelectedAzureWebSite();
            if (webSite == null) {
                throw new NotSupportedException();
            }

            Action<Task<bool>> onAttach = null;
            onAttach = (attachTask) => {
                if (!attachTask.Result) {
                    string msg = string.Format(
                        "Could not attach to node.exe process on Azure web site at {0}\r\n\r\n" +
                        "Make sure that the web site is running and servicing requests, and that the websocket debugging proxy is correctly registered in web.config",
                        webSite.Uri);
                    if (MessageBox.Show(msg, null, MessageBoxButtons.RetryCancel, MessageBoxIcon.Error) == DialogResult.Retry) {
                        AttachWorker(webSite).ContinueWith(onAttach);
                    }
                }
            };

            // We will need to do a bunch of async calls here, and they will deadlock if the UI thread is
            // blocked, so we can't use Wait() or Result, and have to let the task run on its own.
            AttachWorker(webSite).ContinueWith(onAttach);
        }

        /// <returns>
        /// Information about the current selected Azure web site node in Solution Explorer, or <c>null</c>
        /// if no node is selected, it's not a website node, or the information could not be retrieved.
        /// </returns>
        private AzureWebSiteInfo GetSelectedAzureWebSite() {
            // Get the current selected node in Solution Explorer.

            var shell = (IVsUIShell)NodejsPackage.GetGlobalService(typeof(SVsUIShell));
            var serverExplorerToolWindowGuid = new Guid(ToolWindowGuids.ServerExplorer);
            IVsWindowFrame serverExplorerFrame;
            shell.FindToolWindow(0, ref serverExplorerToolWindowGuid, out serverExplorerFrame);
            if (serverExplorerFrame == null) {
                return null;
            }

            object obj;
            serverExplorerFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out obj);
            var serverExplorerHierWnd = obj as IVsUIHierarchyWindow;
            if (serverExplorerHierWnd == null) {
                return null;
            }

            IntPtr hierPtr;
            uint itemid;
            IVsMultiItemSelect mis;
            serverExplorerHierWnd.GetCurrentSelection(out hierPtr, out itemid, out mis);
            if (hierPtr == IntPtr.Zero) {
                return null;
            }

            IVsHierarchy hier;
            try {
                hier = (IVsHierarchy)Marshal.GetObjectForIUnknown(hierPtr);
            } finally {
                Marshal.Release(hierPtr);
            }

            // Get the browse object of that node - this is the object that exposes properties to show in the Properties window.

            hier.GetProperty(itemid, (int)__VSHPROPID.VSHPROPID_SelContainer, out obj);
            var selCtr = obj as ISelectionContainer;
            if (selCtr == null) {
                return null;
            }

            var objs = new object[1];
            selCtr.GetObjects((uint)Microsoft.VisualStudio.Shell.Interop.Constants.GETOBJS_SELECTED, 1, objs);
            obj = objs[0];
            if (obj == null) {
                return null;
            }

            // We need to find out whether this is an Azure website object. We can't do a type check because the type of the 
            // browse object is private. We can, however, query for properties with specific names, and we can check the types
            // of those properties. In particular, WebSiteState is a public enum type that is a part of Azure Explorer public
            // contract, so we can check for it, and we can be reasonably sure that it is only exposed by web site nodes.

            var statusProp = obj.GetType().GetProperty("Status");
            if (statusProp == null ||
                statusProp.PropertyType.FullName != "Microsoft.VisualStudio.Web.WindowsAzure.Contracts.WebSiteState"
            ) {
                return null;
            }

            // Is the web site running?
            int status = (int)statusProp.GetValue(obj);
            if (status != 1) {
                return null;
            }

            // Get the URI
            var urlProp = obj.GetType().GetProperty("Url");
            if (urlProp == null || urlProp.PropertyType != typeof(string)) {
                return null;
            }
            Uri uri;
            if (!Uri.TryCreate((string)urlProp.GetValue(obj), UriKind.Absolute, out uri)) {
                return null;
            }

            // Get Azure subscription ID
            var subIdProp = obj.GetType().GetProperty("SubscriptionID");
            if (subIdProp == null || subIdProp.PropertyType != typeof(string)) {
                return null;
            }
            string subscriptionId = (string)subIdProp.GetValue(obj);

            return new AzureWebSiteInfo(uri, subscriptionId);
        }

        private async Task<bool> AttachWorker(AzureWebSiteInfo webSite) {
            // Get path (relative to site URL) for the debugger endpoint.
            XDocument webConfig;
            try {
                webConfig = await GetWebConfig(webSite);
            } catch (WebException) {
                return false;
            } catch (IOException) {
                return false;
            } catch (XmlException) {
                return false;
            }
            if (webConfig == null) {
                return false;
            }

            var path =
                (from add in webConfig.Elements("configuration").Elements("system.webServer").Elements("handlers").Elements("add")
                 let type = (string)add.Attribute("type")
                 where type != null
                 let components = type.Split(',')
                 where components[0].Trim() == "Microsoft.NodejsTools.Debugger.WebSocketProxy"
                 select (string)add.Attribute("path")
                ).FirstOrDefault();
            if (path == null) {
                return false;
            }

            try {
                AttachDebugger(new UriBuilder(webSite.Uri) { Scheme = (webSite.Uri.Scheme == "https") ? "wss" : "ws", Path = path }.Uri);
                return true;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Retrieves web.config for a given Azure web site.
        /// </summary>
        /// <returns>XML document with the contents of web.config, or <c>null</c> if it could not be retrieved.</returns>
        private async Task<XDocument> GetWebConfig(AzureWebSiteInfo webSite) {
            var publishXml = await GetPublishXml(webSite);
            if (publishXml == null) {
                return null;
            }

            // Get FTP publish URL and credentials from publish settings.

            var publishProfile = publishXml.Elements("publishData").Elements("publishProfile").FirstOrDefault(el => (string)el.Attribute("publishMethod") == "FTP");
            if (publishProfile == null) {
                return null;
            }

            var publishUrl = (string)publishProfile.Attribute("publishUrl");
            var userName = (string)publishProfile.Attribute("userName");
            var userPwd = (string)publishProfile.Attribute("userPWD");
            if (publishUrl == null || userName == null || userPwd == null) {
                return null;
            }

            // Get web.config for the site via FTP.

            if (!publishUrl.EndsWith("/")) {
                publishUrl += "/";
            }
            publishUrl += "web.config";

            Uri webConfigUri;
            if (!Uri.TryCreate(publishUrl, UriKind.Absolute, out webConfigUri)) {
                return null;
            }

            var request = WebRequest.Create(webConfigUri) as FtpWebRequest;
            // Check that this is actually an FTP request, in case we get some valid but weird URL back.
            if (request == null) {
                return null;
            }
            request.Credentials = new NetworkCredential(userName, userPwd);

            using (var response = await request.GetResponseAsync())
            using (var stream = response.GetResponseStream()) {
                // There is no XDocument.LoadAsync, but we want the networked I/O at least to be async, even if parsing is not.
                var xmlData = new MemoryStream();
                await stream.CopyToAsync(xmlData);
                xmlData.Position = 0;
                return XDocument.Load(xmlData);
            }
        }

        /// <summary>
        /// Retrieves the publish settings file (.pubxml) for the given Azure web site.
        /// </summary>
        /// <returns>XML document with the contents of .pubxml, or <c>null</c> if it could not be retrieved.</returns>
        private async Task<XDocument> GetPublishXml(AzureWebSiteInfo webSiteInfo) {
            // To build the publish settings request URL, we need to know subscription ID, site name, and web region to which it belongs,
            // but we only have subscription ID and the public URL of the site at this point. Use the Azure web site service to look up
            // the site from those two, and retrieve the missing info.

            // The code below must avoid doing anything that would result in types from the Azure Tools contract assemblies being
            // referenced in any way in signatures of any members of any classes in this assembly. Because the contract assembly can
            // be be missing (if Azure SDK is not installed), such references will cause Reflection to break, which will break MEF
            // and block all our MEF exports. The main danger is classes generated by the compiler to support constructs such as
            // lambdas and await. To that extent, the following are not allowed in the code below:
            //
            //   - local variables of Azure types (become fields if captured by a lambda or used across await);
            //   - lambda arguments of Azure types (become arguments on generated method), and LINQ expressions that would implicitly
            //     produce such lambdas;
            //   - await on a Task that has result of an Azure type (produces a field of type TaskAwaiter<TResult>);
            //
            // To make it easier to verify, "var" and LINQ syntactic sugar should be avoided completely, and all lambdas should
            // have the types of arguments explicitly specified. For "await", cast the return type of any Azure-type-returning
            // async method to untyped Task first before awaiting, then use an explicit cast to Task<T> to read Result. The only
            // mentions of Azure types in the body of the method should be in casts.

            object webSiteServices = NodejsPackage.GetGlobalService(typeof(IVsAzureWebSiteServices));
            if (webSiteServices == null) {
                return null;
            }

            object webSiteService = ((IVsAzureWebSiteServices)webSiteServices).GetAzureWebSitesService();
            if (webSiteService == null) {
                return null;
            }

            Task getSubscriptionsTask = ((IAzureWebSitesService)webSiteService).GetSubscriptionsAsync();
            await getSubscriptionsTask;
            IEnumerable<object> subscriptions = ((Task<List<IAzureSubscription>>)getSubscriptionsTask).Result;

            object subscription = subscriptions.FirstOrDefault((object sub) => ((IAzureSubscription)sub).SubscriptionId == webSiteInfo.SubscriptionId);
            if (subscription == null) {
                return null;
            }

            Task getWebSitesTask = ((IAzureSubscription)subscription).GetWebSitesAsync(false);
            await getWebSitesTask;
            IEnumerable<object> webSites = ((Task<List<IAzureWebSite>>)getWebSitesTask).Result;

            object webSite = webSites.FirstOrDefault((object ws) => {
                Uri browseUri;
                Uri.TryCreate(((IAzureWebSite)ws).BrowseURL, UriKind.Absolute, out browseUri);
                return browseUri != null && browseUri.Equals(webSiteInfo.Uri);
            });
            if (webSite == null) {
                return null;
            }

            // Prepare a web request to get the publish settings.
            // See http://msdn.microsoft.com/en-us/library/windowsazure/dn166996.aspx
            string requestPath = string.Format(
                "{0}/services/WebSpaces/{1}/sites/{2}/publishxml",
                ((IAzureWebSite)webSite).Subscription.SubscriptionId,
                ((IAzureWebSite)webSite).WebSpace,
                ((IAzureWebSite)webSite).Name);
            Uri requestUri = new Uri(((IAzureSubscription)subscription).ServiceManagementEndpointUri, requestPath);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUri);
            request.Method = "GET";
            request.ContentType = "application/xml";
            request.Headers.Add("x-ms-version", "2010-10-28");

            // Set up authentication for the request, depending on whether the associated subscription context is 
            // account-based or certificate-based.
            object context = ((IAzureSubscription)subscription).AzureCredentials;
            if (context is IAzureAuthenticationCertificateSubscriptionContext) {
                X509Certificate2 cert = await ((IAzureAuthenticationCertificateSubscriptionContext)context).AuthenticationCertificate.GetCertificateFromStoreAsync();
                request.ClientCertificates.Add(cert);
            } else if (context is IAzureUserAccountSubscriptionContext) {
                string authHeader = await ((IAzureUserAccountSubscriptionContext)context).GetAuthenticationHeaderAsync(false);
                request.Headers.Add(HttpRequestHeader.Authorization, authHeader);
            } else {
                return null;
            }

            using (WebResponse response = await request.GetResponseAsync())
            using (Stream stream = response.GetResponseStream()) {
                // There is no XDocument.LoadAsync, but we want the networked I/O at least to be async, even if parsing is not.
                Stream xmlData = new MemoryStream();
                await stream.CopyToAsync(xmlData);
                xmlData.Position = 0;
                return XDocument.Load(xmlData);
            }
        }

        private unsafe void AttachDebugger(Uri uri) {
            var debugger = (IVsDebugger2)NodejsPackage.GetGlobalService(typeof(SVsShellDebugger));
            var debugInfo = new VsDebugTargetInfo2();

            var pDebugEngines = stackalloc Guid[1];
            pDebugEngines[0] = AD7Engine.DebugEngineGuid;

            debugInfo.cbSize = (uint)Marshal.SizeOf(typeof(VsDebugTargetInfo2));
            debugInfo.dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_AlreadyRunning;
            debugInfo.guidLaunchDebugEngine = AD7Engine.DebugEngineGuid;
            debugInfo.dwDebugEngineCount = 1;
            debugInfo.pDebugEngines = (IntPtr)pDebugEngines;
            debugInfo.guidPortSupplier = NodeRemoteDebugPortSupplier.PortSupplierGuid;
            debugInfo.bstrPortName = uri.ToString();
            debugInfo.dwProcessId = NodeRemoteDebugProcess.RemoteId;
            debugInfo.bstrExe = (char)0 + "0x" + debugInfo.dwProcessId.ToString("X"); // this must be set to NUL + process ID in hex when DLO_AlreadyRunning is specified
            debugInfo.LaunchFlags = 0;

            var pDebugInfo = stackalloc byte[Marshal.SizeOf(debugInfo)];
            Marshal.StructureToPtr(debugInfo, (IntPtr)pDebugInfo, false);
            Marshal.ThrowExceptionForHR(debugger.LaunchDebugTargets2(1, (IntPtr)pDebugInfo));
        }


        /// <summary>
        /// Information about an Azure Web Site node in Server Explorer.
        /// </summary>
        private class AzureWebSiteInfo {
            public readonly Uri Uri;
            public readonly string SubscriptionId;

            public AzureWebSiteInfo(Uri uri, string subscriptionId) {
                Uri = uri;
                SubscriptionId = subscriptionId;
            }
        }
    }
}
