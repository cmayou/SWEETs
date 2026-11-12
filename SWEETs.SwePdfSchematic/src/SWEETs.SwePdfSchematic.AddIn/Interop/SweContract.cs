using System.Collections.Generic;
using System.Linq;

namespace SWEETs.SwePdfSchematic.AddIn.Interop
{
    /// <summary>
    /// Every EwAPI member the add-in calls, with candidate names in priority order.
    /// ✓ = verified against the EwAPI type library embedded in SOLIDWORKS Electrical 2026's EwAPIX.dll
    /// (names, argument lists and return types). <see cref="SweApi.CheckContract"/> re-checks them at startup,
    /// so a future SWE release that renames a member shows up on the Environment card: put the new name
    /// first in that member's candidate list. Nothing else needs to change.
    /// </summary>
    public static class SweContract
    {
        public sealed class Member
        {
            public string Iface;
            public string[] Names;
            public bool Optional;
            public override string ToString() => $"{Iface}.{Names[0]}";
        }

        private static readonly List<Member> All = new List<Member>();
        private static Member M(string iface, bool optional, params string[] names)
        {
            var m = new Member { Iface = iface, Names = names, Optional = optional };
            All.Add(m);
            return m;
        }

        public static IEnumerable<(string iface, string[] names, bool optional)> Members => All.Select(m => (m.Iface, m.Names, m.Optional));

        // ---- application / add-in plumbing
        public static readonly Member FactoryGetApplication = M("IEwInteropFactoryX", false, "getEwApplication");                // ✓ (string key, out err); declared on base IInteropFactoryX
        public static readonly Member AppGetEnvironment = M("IEwApplicationX", false, "getEwEnvironment");                        // ✓
        public static readonly Member AppOpenProjectId = M("IEwApplicationX", false, "openEwProjectID");                          // ✓
        public static readonly Member AppGetRibbon = M("IEwApplicationX", true, "getEwRibbon");                                   // ✓
        public static readonly Member AppGetCommandManager = M("IEwApplicationX", false, "getEwCommandManager");                   // ✓
        public static readonly Member CmdAdd = M("IEwCommandManagerX", false, "addCommand");                                      // ✓
        public static readonly Member CmdRemove = M("IEwCommandManagerX", true, "removeCommand");                                 // ✓
        public static readonly Member RibbonAddTab = M("IEwRibbonX", true, "addTab");                                             // ✓ (caption, out err)
        public static readonly Member RibbonTabAddGroup = M("IEwRibbonTabX", true, "addGroup");                                   // ✓ (caption, out err)
        public static readonly Member RibbonGroupAddControl = M("IEwRibbonGroupX", true, "addControl");                           // ✓ (EwRibbonControlType, command, out err)
        public static readonly Member RibbonTabRemove = M("IEwRibbonTabX", true, "remove");                                       // ✓
        public static readonly Member ControlSetCaption = M("IEwRibbonControlX", true, "setCaption");                             // ✓
        public static readonly Member ControlSetToolTip = M("IEwRibbonControlX", true, "setToolTip");                             // ✓
        public static readonly Member ControlSetIcon = M("IEwRibbonControlX", true, "setIconFromImage");                          // ✓ (path, EwRibbonButtonSize, uint mask)

        // ---- symbol library (mapping picker); all optional: the picker falls back to learned names
        public static readonly Member EnvGetSymbolManager = M("IEwEnvironmentX", true, "getEwSymbolManager");                    // ✓
        public static readonly Member EnvGetClassificationManager = M("IEwEnvironmentX", true, "getEwClassificationManager");   // ✓
        public static readonly Member EnvGetFolderPath = M("IEwEnvironmentX", true, "getFolderPath");                            // ✓ (EwEnvironmentFolderPathValue)
        public static readonly Member SymMgrGetArray = M("IEwSymbolManagerX", true, "getEwSymbolArray");                         // ✓ VARIANT array of IEwSymbolX
        public static readonly Member SymGetName = M("IEwSymbolX", true, "getName");                                             // ✓
        public static readonly Member SymGetDescription = M("IEwSymbolX", true, "getDescription");                               // ✓ (lang)
        public static readonly Member SymGetStandard = M("IEwSymbolX", true, "getStandard");                                     // ✓
        public static readonly Member SymGetLibraryCode = M("IEwSymbolX", true, "getLibraryCode");                               // ✓
        public static readonly Member SymGetManufacturer = M("IEwSymbolX", true, "getManufacturer");                             // ✓
        public static readonly Member SymGetType = M("IEwSymbolX", true, "getEwSymbolType");                                     // ✓
        public static readonly Member SymGetClassId = M("IEwSymbolX", true, "getClassID");                                       // ✓ diagnostics only
        public static readonly Member SymGetClassNodeId = M("IEwSymbolX", true, "getClassNodeID");                               // ✓ node in the classification tree
        public static readonly Member SymGetCircuitCount = M("IEwSymbolX", true, "getEwSymbolCircuitCount");                     // ✓
        public static readonly Member SymGetMeasurementType = M("IEwSymbolX", true, "getEwMeasurementType");                     // ✓ kMeasurementTypeMetric / Imperial / Unknown
        public static readonly Member SymGetPointCount = M("IEwSymbolX", true, "getEwSymbolPointCount");                         // ✓
        public static readonly Member SymGetClassification = M("IEwSymbolX", true, "getClassification");                         // ✓ the symbol's own tree
        public static readonly Member ClsMgrGetArray = M("IEwClassificationManagerX", true, "getEwClassArray");                  // ✓ all classifications
        public static readonly Member ClsMgrGetClassification = M("IEwClassificationManagerX", true, "getEwClassification");     // ✓ (EwClassificationType)
        public static readonly Member ClassificationGetId = M("IEwClassificationX", true, "getID");                              // ✓
        public static readonly Member ClassificationGetClassArray = M("IEwClassificationX", true, "getEwClassArray");            // ✓
        public static readonly Member ClassGetId = M("IEwClassX", true, "getID");                                                // ✓
        public static readonly Member ClassGetDescription = M("IEwClassX", true, "getDescription");                              // ✓ (lang)
        public static readonly Member ClassGetParentId = M("IEwClassX", true, "getParentID");                                    // ✓
        public static readonly Member ClassGetClassId = M("IEwClassX", true, "getClassID");                                      // ✓ calibration candidate

        // ---- projects
        public static readonly Member EnvGetProjectManager = M("IEwEnvironmentX", false, "getEwProjectManager");                 // ✓
        public static readonly Member PmNewProject = M("IEwProjectManagerX", false, "newEwProject");                             // ✓
        public static readonly Member PmUnarchive = M("IEwProjectManagerX", false, "unarchive");                                 // ✓
        public static readonly Member PmFindById = M("IEwProjectManagerX", false, "findEwProjectByID");                          // ✓
        public static readonly Member ProjSetName = M("IEwProjectX", false, "setName");                                          // ✓
        public static readonly Member ProjSetDescription = M("IEwProjectX", true, "setDescription");                              // ✓ (lang, text)
        public static readonly Member ProjInsertFromTemplate = M("IEwProjectX", false, "insertFromTemplate");                    // ✓
        public static readonly Member ObjGetId = M("IEwObjectX", false, "getID");                                                // ✓
        public static readonly Member ObjInsert = M("IEwObjectBaseX", false, "insert");                                          // ✓
        public static readonly Member ObjUpdate = M("IEwObjectBaseX", true, "update");                                           // ✓

        // ---- books & files
        public static readonly Member ProjGetBookManager = M("IEwProjectX", false, "getEwProjectBookManager");                   // ✓
        public static readonly Member BookArray = M("IEwProjectBookManagerX", false, "getEwProjectBookArray");                   // ✓
        public static readonly Member BookNew = M("IEwProjectBookManagerX", true, "newEwProjectBook");                           // ✓
        public static readonly Member BookSetDescription = M("IEwProjectBookX", true, "setDescription");                         // ✓ (lang, text)
        public static readonly Member ProjGetFileManager = M("IEwProjectX", false, "getEwProjectFileManager");                   // ✓
        public static readonly Member FileNew = M("IEwProjectFileManagerX", false, "newProjectFile");                            // ✓
        public static readonly Member FileSetType = M("IEwProjectFileX", false, "setFileType");                                   // ✓
        public static readonly Member FileSetBook = M("IEwProjectFileX", false, "setEwProjectBookID");                            // ✓
        public static readonly Member FileSetDescription = M("IEwProjectFileX", true, "setDescription");                          // ✓ (lang, text)
        public static readonly Member FileSetOriginalPath = M("IEwProjectFileX", true, "setOriginalFilePath");                    // ✓
        public static readonly Member FileOpen = M("IEwProjectFileX", true, "open");                                              // ✓
        public static readonly Member FileClose = M("IEwProjectFileX", true, "close");                                            // ✓

        // ---- components & symbols
        public static readonly Member ProjGetComponentManager = M("IEwProjectX", false, "getEwProjectComponentManager");         // ✓
        public static readonly Member ComponentNew = M("IEwProjectComponentManagerX", false, "newEwProjectComponent");           // ✓ (no args)
        public static readonly Member ComponentSetTag = M("IEwProjectTagObjectX", false, "setTag");                              // ✓
        public static readonly Member ComponentSetDescription = M("IEwProjectComponentX", true, "setDescription");               // ✓ (lang, text)
        public static readonly Member FileNewSymbol = M("IEwProjectFileX", false, "newEwProjectSymbol");                          // ✓ symbol is created on its page
        public static readonly Member FileNewSymbolOfType = M("IEwProjectFileX", true, "newEwProjectSymbolFromSymbolType");      // ✓ (EwSymbolType) must match the library symbol
        public static readonly Member FileCanInsertSymbol = M("IEwProjectFileX", true, "canInsertSymbol");                        // ✓
        public static readonly Member SymbolSetName = M("IEwProjectSymbolX", false, "setEwSymbolName");                           // ✓
        public static readonly Member SymbolSetObject = M("IEwProjectSymbolX", false, "setObjectID");                             // ✓
        public static readonly Member SymbolSetX = M("IEwProjectSymbolX", false, "setXPosition");                                 // ✓
        public static readonly Member SymbolSetY = M("IEwProjectSymbolX", false, "setYPosition");                                 // ✓
        public static readonly Member SymbolSetRotation = M("IEwProjectSymbolX", false, "setRotationAngle");                      // ✓
        public static readonly Member SymbolSetXScale = M("IEwProjectSymbolX", false, "setXScale");                               // ✓ (double)
        public static readonly Member SymbolSetYScale = M("IEwProjectSymbolX", false, "setYScale");                               // ✓ (double)
        public static readonly Member SymbolPoints = M("IEwProjectSymbolX", false, "getEwProjectSymbolPointArray");              // ✓
        public static readonly Member PointGetPosition = M("IEwProjectSymbolPointX", false, "getPointPosition");                 // ✓ returns IEwPointX
        public static readonly Member PointGetX = M("IEwPointX", false, "getXCoordinate");                                       // ✓
        public static readonly Member PointGetY = M("IEwPointX", false, "getYCoordinate");                                       // ✓

        // ---- manufacturer parts (catalog = environment, assignment = project component)
        public static readonly Member EnvGetPartManager = M("IEwEnvironmentX", true, "getEwManufacturerPartManager");            // ✓
        public static readonly Member EnvGetCableRefManager = M("IEwEnvironmentX", true, "getEwCableReferenceManager");          // ✓
        public static readonly Member PartMgrFind = M("IEwManufacturerPartManagerX", true, "findByManufacturerAndReference");    // ✓ (mfr, ref)
        public static readonly Member PartMgrNew = M("IEwManufacturerPartManagerX", true, "newEwManufacturerPart");              // ✓
        public static readonly Member PartMgrNewFilters = M("IEwManufacturerPartManagerX", true, "newEwManufacturerPartFilters"); // ✓
        public static readonly Member PartFiltersSet = M("IEwManufacturerPartFiltersX", true, "setFilterTypeValue");             // ✓ (EwManufacturerPartFilterType, value)
        public static readonly Member PartFiltersGet = M("IEwManufacturerPartFiltersX", true, "getManufacturerPartArray");       // ✓
        public static readonly Member PartGetManufacturer = M("IEwManufacturerPartX", true, "getManufacturer");                  // ✓
        public static readonly Member PartGetReference = M("IEwManufacturerPartX", true, "getReference");                        // ✓
        public static readonly Member PartSetManufacturer = M("IEwManufacturerPartX", true, "setManufacturer");                  // ✓
        public static readonly Member PartSetReference = M("IEwManufacturerPartX", true, "setReference");                        // ✓
        public static readonly Member PartSetDescription = M("IEwManufacturerPartX", true, "setDescription");                    // ✓ (lang, text)
        public static readonly Member PartSetArticleNumber = M("IEwManufacturerPartX", true, "setArticleNumber");                // ✓
        public static readonly Member PartSetType = M("IEwManufacturerPartX", true, "setEwManufacturerPartType");                // ✓ (EwManufacturerPartType)
        public static readonly Member PartSetClassNodeId = M("IEwManufacturerPartX", true, "setClassNodeID");                    // ✓
        public static readonly Member PartSetLibraryCode = M("IEwManufacturerPartX", true, "setLibraryCode");                    // ✓
        public static readonly Member PartGetCircuitCount = M("IEwManufacturerPartX", true, "getEwManufacturerPartCircuitCount");  // ✓
        public static readonly Member PartAddCircuit = M("IEwManufacturerPartX", true, "addEwManufacturerPartCircuit");            // ✓ (circuit type key code)
        public static readonly Member PartCircuitSetSymbolName = M("IEwManufacturerPartCircuitX", true, "setSymbolName");         // ✓
        public static readonly Member PartCircuitSetSymbolNumber = M("IEwManufacturerPartCircuitX", true, "setSymbolNumber");     // ✓
        public static readonly Member PartCircuitSetSymbolCircuit = M("IEwManufacturerPartCircuitX", true, "setSymbolCircuitNumber"); // ✓
        public static readonly Member PartCircuitAddTerminal = M("IEwManufacturerPartCircuitX", true, "addEwManufacturerPartTerminal"); // ✓
        public static readonly Member PartTerminalSetText = M("IEwManufacturerPartTerminalX", true, "setText");                  // ✓ terminal designation
        public static readonly Member SymbolCircuits = M("IEwProjectSymbolX", true, "getEwProjectSymbolCircuitArray");          // ✓
        public static readonly Member SymbolCircuitKeyCode = M("IEwProjectSymbolCircuitX", true, "getKeyCode");                 // ✓ circuit type
        public static readonly Member SymbolCircuitNumber = M("IEwProjectSymbolCircuitX", true, "getCircuitNumber");            // ✓
        public static readonly Member PointGetCircuitNumber = M("IEwProjectSymbolPointX", true, "getCircuitNumber");            // ✓
        public static readonly Member PointGetTerminalNumber = M("IEwProjectSymbolPointX", true, "getTerminalNumber");          // ✓
        public static readonly Member PointGetPointNumber = M("IEwProjectSymbolPointX", true, "getPointNumber");                // ✓
        public static readonly Member CableRefMgrFind = M("IEwCableReferenceManagerX", true, "findByManufacturerAndReference");  // ✓
        public static readonly Member CableRefMgrNew = M("IEwCableReferenceManagerX", true, "newEwCableReference");             // ✓
        public static readonly Member CableRefGetManufacturer = M("IEwCableReferenceX", true, "getManufacturer");               // ✓
        public static readonly Member CableRefGetReference = M("IEwCableReferenceX", true, "getReference");                     // ✓
        public static readonly Member CableRefSetManufacturer = M("IEwCableReferenceX", true, "setManufacturer");               // ✓
        public static readonly Member CableRefSetReference = M("IEwCableReferenceX", true, "setReference");                     // ✓
        public static readonly Member CableRefSetDescription = M("IEwCableReferenceX", true, "setDescription");                 // ✓ (lang, text)
        public static readonly Member CableRefSetArticleNumber = M("IEwCableReferenceX", true, "setArticleNumber");             // ✓
        public static readonly Member ComponentFindById = M("IEwProjectComponentManagerX", true, "findEwProjectComponentByID");  // ✓
        public static readonly Member ComponentSetType = M("IEwProjectComponentX", true, "setType");                             // ✓ (EwComponentType)
        public static readonly Member ComponentSetParent = M("IEwProjectComponentX", true, "setParentID");                       // ✓ terminal → strip
        public static readonly Member ComponentSetClassNodeId = M("IEwProjectComponentX", true, "setClassNodeID");               // ✓
        public static readonly Member ComponentSetClassId = M("IEwProjectComponentX", true, "setClassID");                       // ✓
        public static readonly Member TagObjSetTagMode = M("IEwProjectTagObjectX", true, "setTagMode");                          // ✓ (EwTagMode)
        public static readonly Member ComponentAssignPart = M("IEwProjectComponentX", true, "assignManufacturerPart");          // ✓ (mfr, ref)
        public static readonly Member ComponentSetLocation = M("IEwProjectComponentX", true, "setLocationID");                  // ✓
        public static readonly Member TagObjGetTag = M("IEwProjectTagObjectX", true, "getTag");                                  // ✓
        public static readonly Member ProjGetLocationManager = M("IEwProjectX", true, "getEwProjectLocationManager");           // ✓
        public static readonly Member LocMgrArray = M("IEwProjectLocationManagerX", true, "getEwProjectLocationArray");          // ✓
        public static readonly Member LocMgrNew = M("IEwProjectLocationManagerX", true, "newEwProjectLocation");                 // ✓
        public static readonly Member LocSetDescription = M("IEwProjectLocationX", true, "setDescription");                      // ✓ (lang, text)
        public static readonly Member ProjGetCableManager = M("IEwProjectX", true, "getEwProjectCableManager");                 // ✓
        public static readonly Member CableMgrNew = M("IEwProjectCableManagerX", true, "newEwProjectCable");                     // ✓
        public static readonly Member CableSetDescription = M("IEwProjectCableX", true, "setDescription");                       // ✓ (lang, text)
        public static readonly Member CableSetArticleNumber = M("IEwProjectCableX", true, "setArticleNumber");                   // ✓
        public static readonly Member CableSetUpstreamLocation = M("IEwProjectCableX", true, "setUpStreamLocationID");          // ✓
        public static readonly Member ProjGetPartManager = M("IEwProjectX", true, "getEwProjectManufacturerPartManager");       // ✓
        public static readonly Member ProjPartNew = M("IEwProjectManufacturerPartManagerX", true, "newEwProjectManufacturerPart"); // ✓
        public static readonly Member ProjPartSetManufacturer = M("IEwProjectManufacturerPartX", true, "setManufacturer");      // ✓
        public static readonly Member ProjPartSetReference = M("IEwProjectManufacturerPartX", true, "setReference");            // ✓
        public static readonly Member ProjPartSetDescription = M("IEwProjectManufacturerPartX", true, "setDescription");        // ✓ (lang, text)
        public static readonly Member ProjPartSetObjectId = M("IEwProjectManufacturerPartX", true, "setObjectID");              // ✓

        // ---- wire style and wire numbers
        public static readonly Member ProjGetWireStyleManager = M("IEwProjectX", true, "getEwProjectWireStyleManagerX");       // ✓
        public static readonly Member WireStyleArray = M("IEwProjectWireStyleManagerX", true, "getEwProjectWireStyleArray");   // ✓
        public static readonly Member WireStyleNew = M("IEwProjectWireStyleManagerX", true, "newEwProjectWireStyle");          // ✓
        public static readonly Member WireStyleGetName = M("IEwProjectWireStyleBase", true, "getName");                         // ✓
        public static readonly Member WireStyleSetName = M("IEwProjectWireStyleBase", true, "setName");                         // ✓
        public static readonly Member WireStyleSetDescription = M("IEwProjectWireStyleBase", true, "setDescription");           // ✓ (lang, text)
        public static readonly Member LineSetWireStyle = M("IEwProjectLineX", true, "setWireStyleID");                          // ✓
        public static readonly Member ProjNewNumberWires = M("IEwProjectX", true, "newEwProjectNumberWires");                   // ✓
        public static readonly Member NumberWiresSetSelectionType = M("IEwProjectNumberWiresX", true, "setSelectionType");     // ✓ (EwSelectionType)
        public static readonly Member NumberWiresProcess = M("IEwProjectNumberWiresX", true, "process");                       // ✓ (EwNumberWireAction)
        public static readonly Member ProjGetWireManager = M("IEwProjectX", true, "getEwProjectWireManager");                  // ✓
        public static readonly Member WireMgrArray = M("IEwProjectWireManagerX", true, "getEwProjectWireArray");               // ✓
        public static readonly Member WireOriginComponent = M("IEwProjectWireX", true, "getOriginComponentID");                // ✓
        public static readonly Member WireDestComponent = M("IEwProjectWireX", true, "getDestinationComponentID");            // ✓
        public static readonly Member WireOriginCircuit = M("IEwProjectWireX", true, "getOriginComponentCircuitID");          // ✓
        public static readonly Member WireDestCircuit = M("IEwProjectWireX", true, "getDestinationComponentCircuitID");      // ✓
        public static readonly Member WireOriginTerminal = M("IEwProjectWireX", true, "getOriginComponentTerminalNumber");    // ✓
        public static readonly Member WireDestTerminal = M("IEwProjectWireX", true, "getDestinationComponentTerminalNumber"); // ✓
        public static readonly Member WireSetTag = M("IEwProjectWireX", true, "setTag");                                       // ✓
        public static readonly Member WireSetTagMode = M("IEwProjectWireX", true, "setTagMode");                               // ✓ (EwTagMode)
        public static readonly Member ComponentCircuits = M("IEwProjectComponentX", true, "getEwProjectComponentCircuitArray"); // ✓
        public static readonly Member CompCircuitSymbolId = M("IEwProjectComponentCircuitX", true, "getSymbolID");             // ✓
        public static readonly Member CompCircuitSymbolCircuit = M("IEwProjectComponentCircuitX", true, "getCircuitSymbolID"); // ✓

        // ---- wires: IEwProjectLineX created from its page (since 2024 SP1)
        public static readonly Member FileNewLine = M("IEwProjectFileX", false, "newEwProjectLine");                              // ✓ (EwLineType, out err)
        public static readonly Member LineSetStartX = M("IEwProjectLineX", false, "setStartPointXPosition");                     // ✓
        public static readonly Member LineSetStartY = M("IEwProjectLineX", false, "setStartPointYPosition");                     // ✓
        public static readonly Member LineSetEndX = M("IEwProjectLineX", false, "setEndPointXPosition");                         // ✓
        public static readonly Member LineSetEndY = M("IEwProjectLineX", false, "setEndPointYPosition");                         // ✓
    }
}
