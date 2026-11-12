using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DrawingParametersModule = SWEETs.SweDrawingParameters.AddIn.Entry.SweAddIn;
using LineStylesModule = SWEETs.SweLineStyles.AddIn.Entry.SweAddIn;
using PdfSchematicModule = SWEETs.SwePdfSchematic.AddIn.Entry.SweAddIn;
using DatasheetModule = DataSheetMfgPartsImport.Addin;
using VendorGridModule = VendorGrid.Addin.VendorGridAddIn;
using ChangeLogModule = ChangeLog.AddIn.ChangeLogAddIn;

namespace SWEETs.AddIn.Entry
{
    /// <summary>One button on the SWEETs tab.</summary>
    public sealed class ToolButton
    {
        public string Command;        // SWE command the button runs
        public string Caption;
        public string ToolTip;
        public string Description;
        public (string size, string file)[] Icons;
    }

    /// <summary>
    /// A SWEETs tool and its ribbon button(s). Most tools have one button (<see cref="Connect"/> returns its command);
    /// a tool with several buttons (Change Log) sets <see cref="ConnectButtons"/> instead, which returns them all.
    /// Turning a tool off in SWEETs Settings hides all of its buttons.
    /// </summary>
    public sealed class ToolDefinition
    {
        public string Id;             // key in settings.ini; never change once released
        public string Caption;        // ribbon button caption
        public string Group;          // ribbon group caption
        public string ToolTip;
        public string Description;    // ribbon description and Settings window text
        public string LogFolder;      // where the tool writes its own log
        public (string size, string file)[] Icons;   // EwRibbonButtonSize name, file under the add-in folder
        public uint IconMask;         // COLORREF treated as transparent by setIconFromImage

        /// <summary>Registers the tool's command; returns the command string the ribbon button runs.</summary>
        public Func<object, string> Connect;
        /// <summary>For tools with several buttons: registers their commands and returns the buttons in ribbon order.</summary>
        public Func<object, List<ToolButton>> ConnectButtons;
        public Action Disconnect;

        // ---- run-time state
        public bool Connected;
        public List<ToolButton> Buttons = new List<ToolButton>();
        public string LastError;
    }

    /// <summary>The SWEETs tools, in ribbon order.</summary>
    public static class ToolCatalog
    {
        public const string DrawingToolsGroup = "Drawing Tools";
        public const string ImportToolsGroup = "Import Tools";
        public const string ExportToolsGroup = "Export Tools";
        public const string ProjectToolsGroup = "Project Tools";
        public const string SettingsGroup = "Settings";

        /// <summary>Ribbon groups, in tab order (the Settings group is always last).</summary>
        public static readonly string[] Groups = { DrawingToolsGroup, ImportToolsGroup, ExportToolsGroup, ProjectToolsGroup };

        private static string Local(params string[] parts) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(parts));
        private static string Roaming(params string[] parts) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.Combine(parts));

        // Icons: the SWEETs tools' own ribbon icons were set with mask 0xFFFFFF (as in their stand-alone versions); the
        // Data Sheet, VendorGrid and Change Log icons carry real transparency and use the magenta mask 0xFF00FF.
        public static List<ToolDefinition> Create() => new List<ToolDefinition>
        {
            new ToolDefinition
            {
                Id = "DrawingParameters",
                Caption = DrawingParametersModule.ButtonCaption,
                Group = DrawingToolsGroup,
                ToolTip = "Update grid, snap, grid zone and ortho on the drawings of the open project",
                Description = DrawingParametersModule.Description,
                LogFolder = Local("SWEETs", "DrawingParameters", "logs"),
                Icons = new[] { ("kButtonSize32", @"Resources\drawing-parameters-32.png") },
                IconMask = 0xFFFFFFu,
                Connect = app => DrawingParametersModule.Connect(app),
                Disconnect = () => DrawingParametersModule.Disconnect(),
            },
            new ToolDefinition
            {
                Id = "LineStyles",
                Caption = LineStylesModule.ButtonCaption,
                Group = DrawingToolsGroup,
                ToolTip = "Edit line styles and the shapes they use",
                Description = LineStylesModule.Description,
                LogFolder = Local("SWEETs", "LineStyles", "logs"),
                Icons = new[] { ("kButtonSize32", @"Resources\line-styles-32.png") },
                IconMask = 0xFFFFFFu,
                Connect = app => LineStylesModule.Connect(app),
                Disconnect = () => LineStylesModule.Disconnect(),
            },
            new ToolDefinition
            {
                Id = "PdfToSchematic",
                Caption = PdfSchematicModule.ButtonCaption,
                Group = ImportToolsGroup,
                ToolTip = "Import a vector PDF schematic and build a SOLIDWORKS Electrical project from it",
                Description = PdfSchematicModule.Description,
                LogFolder = Local("SWEETs", "PdfSchematic", "logs"),
                Icons = new[] { ("kButtonSize32", @"Resources\pdf-to-schematic-32.png") },
                IconMask = 0xFFFFFFu,
                Connect = app => PdfSchematicModule.Connect(app),
                Disconnect = () => PdfSchematicModule.Disconnect(),
            },
            new ToolDefinition
            {
                Id = "DatasheetImporter",
                Caption = DatasheetModule.ButtonCaption,
                Group = ImportToolsGroup,
                ToolTip = "Read a manufacturer data sheet and add its parts to the manufacturer parts manager",
                Description = "Reads a manufacturer data sheet (a PDF or a photo), lets you review the extracted fields, then adds the parts to the manufacturer parts manager under a library.",
                LogFolder = Roaming("DataSheetMfgPartsImport"),
                Icons = new[]
                {
                    ("kButtonSize16", @"Resources\datasheet-importer-16.png"),
                    ("kButtonSize32", @"Resources\datasheet-importer-32.png"),
                    ("kButtonSize64", @"Resources\datasheet-importer-64.png"),
                },
                IconMask = 0x00FF00FFu,
                Connect = app => DatasheetModule.Connect(app),
                Disconnect = () => DatasheetModule.Disconnect(),
            },
            new ToolDefinition
            {
                Id = "VendorGrid",
                Caption = VendorGridModule.ButtonCaption,
                Group = ExportToolsGroup,
                ToolTip = VendorGridModule.ToolTip,
                Description = VendorGridModule.Description,
                LogFolder = Local("VendorGrid"),
                Icons = new[]
                {
                    ("kButtonSize16", @"Resources\vendorgrid-16.png"),
                    ("kButtonSize32", @"Resources\vendorgrid-32.png"),
                    ("kButtonSize64", @"Resources\vendorgrid-64.png"),
                },
                IconMask = 0x00FF00FFu,
                Connect = app => VendorGridModule.Connect(app),
                Disconnect = () => VendorGridModule.Disconnect(),
            },
            new ToolDefinition
            {
                Id = "ChangeLog",
                Caption = ChangeLogModule.ButtonCaption,
                Group = ProjectToolsGroup,
                ToolTip = "Open the change log for the active project",
                Description = ChangeLogModule.Description,
                LogFolder = Local("SWE ChangeLog"),
                IconMask = 0x00FF00FFu,
                // Six buttons, as stand-alone: Change Log, Compare, Capture Changes, Export to Excel, Verify, Settings.
                ConnectButtons = app => ChangeLogModule.Connect(app).Select(b => new ToolButton
                {
                    Command = b.Command,
                    Caption = b.Caption,
                    ToolTip = b.ToolTip,
                    Description = b.ToolTip,
                    Icons = new[] { ("kButtonSize16", b.Icon16), ("kButtonSize32", b.Icon32) },
                }).ToList(),
                Disconnect = () => ChangeLogModule.Disconnect(),
            },
        };
    }
}
