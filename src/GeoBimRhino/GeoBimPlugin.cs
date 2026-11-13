// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

[assembly: Guid("29a087ec-708c-4259-90fe-414d6c4e3880")]
[assembly: PlugInDescription(DescriptionType.Address, "https://viewer.geobim.app")]
[assembly: PlugInDescription(DescriptionType.Organization, "geoBIM.app")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://geobim.app")]
[assembly: PlugInDescription(DescriptionType.Email, "info@geobim.app")]

namespace GeoBimRhino
{
    /// <summary>
    /// Plugin settings live in Rhino's persistent plugin settings (per user):
    /// server URL, plugin token (created in the geoBIM.app Account panel),
    /// EPSG code for models drawn in map coordinates ("auto" = detect).
    /// </summary>
    [Guid("2629d057-640c-483e-996d-942ddc6eb616")]
    public class GeoBimPlugin : PlugIn
    {
        public const string DefaultServer = "https://viewer.geobim.app";

        public static GeoBimPlugin Instance { get; private set; }

        public GeoBimPlugin()
        {
            Instance = this;
        }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        public const string ToolbarGroupName = "geoBIM.app";

        /// <summary>GeoBimRhino.rui next to the .rhp</summary>
        public static string ToolbarPath => System.IO.Path.ChangeExtension(typeof(GeoBimPlugin).Assembly.Location, ".rui");

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Rhino opens a .rui named like the plugin only on the very first load; a plugin
            // that was already installed before the toolbar existed opens it here, once.
            if (!Settings.GetBool("toolbarShown", false)) Rhino.RhinoApp.Idle += OpenToolbarOnce;
            return LoadReturnCode.Success;
        }

        void OpenToolbarOnce(object sender, EventArgs e)
        {
            Rhino.RhinoApp.Idle -= OpenToolbarOnce;
            if (ShowToolbar()) Settings.SetBool("toolbarShown", true);
        }

        /// <summary>Opens GeoBimRhino.rui if needed and shows the geoBIM.app toolbar group</summary>
        public static bool ShowToolbar()
        {
            try
            {
                var path = ToolbarPath;
                if (!System.IO.File.Exists(path)) return false;
                var files = Rhino.RhinoApp.ToolbarFiles;
                var file = files.FindByPath(path) ?? files.Open(path);
                if (file == null) return false;
                var group = file.GetGroup(ToolbarGroupName);
                if (group == null) return false;
                group.Visible = true;
                return true;
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("geoBIM.app: toolbar could not be opened — " + ex.Message);
                return false;
            }
        }

        public string Server
        {
            get => Settings.GetString("server", DefaultServer).TrimEnd('/');
            set => Settings.SetString("server", (value ?? DefaultServer).Trim().TrimEnd('/'));
        }

        public string Token
        {
            get => Settings.GetString("token", "");
            set => Settings.SetString("token", (value ?? "").Trim());
        }

        /// <summary>"auto" or an EPSG code such as "25832"</summary>
        public string Epsg
        {
            get => Settings.GetString("epsg", "auto");
            set => Settings.SetString("epsg", string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim());
        }

        /// <summary>EPSG used when coordinates look like UTM without zone prefix</summary>
        public int DefaultUtmEpsg
        {
            get => Settings.GetInteger("defaultUtmEpsg", 25832);
            set => Settings.SetInteger("defaultUtmEpsg", value);
        }

        public static string MaskedToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return "(none)";
            return token.Length > 8 ? token.Substring(0, 8) + "…" : "…";
        }

        public static string PluginVersion => typeof(GeoBimPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
