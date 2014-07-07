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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.NodejsTools {
    class PkgCmdId {
        public const int cmdidReplWindow                    = 0x201;
        public const int cmdidOpenRemoteDebugProxyFolder    = 0x202;
        public const int cmdidSetAsNodejsStartupFile        = 0x203;

        public const int cmdidSurveyNews                    = 0x204;
        public const int cmdidImportWizard                  = 0x205;
        public const int cmdidOpenRemoteDebugDocumentation  = 0x206;
        public const int cmdidNpmManageModules              = 0x207;
        public const int cmdidNpmInstallModules             = 0x208;
        public const int cmdidNpmUpdateModules              = 0x209;
        public const int cmdidNpmUninstallModule            = 0x20A;
        public const int cmdidNpmInstallSingleMissingModule = 0x20B;
        public const int cmdidNpmUpdateSingleModule         = 0x20C;
        public const int cmdidDiagnostics                   = 0x20E;

        public const uint cmdidAzureExplorerAttachNodejsDebugger = 0x20D;

        public const uint menuIdNPM = 0x1000;
    }
}
