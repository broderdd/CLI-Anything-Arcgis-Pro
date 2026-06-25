using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ProSimpleMapExport
{
    /// <summary>
    /// ADDITIVE live-authoring commands for the bridge. Lives in its own partial so the
    /// original BridgeServer.cs (transport, auth, the existing 5 commands) is untouched.
    ///
    /// Design: a generic get_cim / set_cim pair is the workhorse — it round-trips the CIM
    /// definition of a layer / the map / a layout / a named layout element as JSON, which
    /// covers definition queries, renames, renderer class-label edits, and datasource
    /// repointing in one mechanism. A few structural commands (clone_layer, create_group,
    /// move_layer, set_visibility, set_layout_text) cover what CIM-set alone can't do cleanly.
    ///
    /// Everything here runs on the MCT (QueuedTask) — Dispatch() in BridgeServer.cs wraps
    /// these the same way as the existing handlers. Strictly current-project scoped.
    /// </summary>
    internal static partial class BridgeServer
    {
        // ---- generic CIM round-trip -------------------------------------------------

        /// <summary>
        /// get_cim — return the CIM definition (as JSON) of a target.
        /// params: target = layer name (default), OR type:"map"|"layout"|"element".
        ///   layer:  {"command":"get_cim","layer":"Wind Turbines"}
        ///   map:    {"command":"get_cim","target":"map"[,"map":"Map name"]}
        ///   layout: {"command":"get_cim","target":"layout","layout":"Layout name"}
        ///   element:{"command":"get_cim","target":"element","layout":"L","element":"Title"}
        /// </summary>
        private static object DoGetCim(JsonElement root)
        {
            string target = Str(root, "target");
            CIMObject cim;
            string kind;
            string name;

            if (string.Equals(target, "map", StringComparison.OrdinalIgnoreCase))
            {
                var map = ResolveMap(root);
                cim = map.GetDefinition();
                kind = "map"; name = map.Name;
            }
            else if (string.Equals(target, "layout", StringComparison.OrdinalIgnoreCase))
            {
                var layout = ResolveLayout(root);
                cim = layout.GetDefinition();
                kind = "layout"; name = layout.Name;
            }
            else if (string.Equals(target, "element", StringComparison.OrdinalIgnoreCase))
            {
                var el = ResolveElement(root, out var layout);
                cim = el.GetDefinition();
                kind = "element"; name = el.Name;
            }
            else
            {
                // default: a layer (by name)
                var layer = ResolveLayer(root, out var map);
                cim = layer.GetDefinition();
                kind = "layer"; name = layer.Name;
            }

            return new
            {
                kind,
                name,
                cimType = cim.GetType().Name,
                cim = JsonSerializer.Deserialize<JsonElement>(cim.ToJson(null))
            };
        }

        /// <summary>
        /// set_cim — replace the CIM definition of a target from JSON.
        /// Pass the full CIM object back under "cim" (same shape get_cim returned).
        /// This single command performs: definition-query edits, layer rename (change
        /// CIMFeatureLayer.Name), renderer class-label edits (CIMUniqueValueRenderer
        /// group/class .Label), and datasource repoint (edit featureTable.dataConnection).
        ///   {"command":"set_cim","layer":"X","cim":{...}}
        ///   {"command":"set_cim","target":"map","cim":{...}}
        ///   {"command":"set_cim","target":"element","layout":"L","element":"Title","cim":{...}}
        /// </summary>
        private static object DoSetCim(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cim", out var cimEl))
                throw new Exception("missing 'cim' (the CIM definition JSON to apply)");
            string cimJson = cimEl.GetRawText();
            string target = Str(root, "target");

            if (string.Equals(target, "map", StringComparison.OrdinalIgnoreCase))
            {
                var map = ResolveMap(root);
                var def = (CIMMap)CIMObject.FromJson(cimJson, null);
                map.SetDefinition(def);
                return new { kind = "map", name = map.Name, applied = true };
            }
            if (string.Equals(target, "layout", StringComparison.OrdinalIgnoreCase))
            {
                var layout = ResolveLayout(root);
                var def = (CIMLayout)CIMObject.FromJson(cimJson, null);
                layout.SetDefinition(def);
                return new { kind = "layout", name = layout.Name, applied = true };
            }
            if (string.Equals(target, "element", StringComparison.OrdinalIgnoreCase))
            {
                var el = ResolveElement(root, out _);
                var def = (CIMElement)CIMObject.FromJson(cimJson, null);
                el.SetDefinition(def);
                return new { kind = "element", name = el.Name, applied = true };
            }

            // default: layer
            var layer = ResolveLayer(root, out _);
            var layerDef = (CIMBaseLayer)CIMObject.FromJson(cimJson, null);
            layer.SetDefinition(layerDef);
            return new { kind = "layer", name = layer.Name, newName = layer.Name, applied = true };
        }

        // ---- structural commands ----------------------------------------------------

        /// <summary>
        /// rename_layer — convenience wrapper over the CIM (kept explicit because it's common).
        ///   {"command":"rename_layer","layer":"Old name","name":"New name"}
        /// </summary>
        private static object DoRenameLayer(JsonElement root)
        {
            string newName = Str(root, "name");
            if (string.IsNullOrWhiteSpace(newName)) throw new Exception("missing 'name' (new layer name)");
            var layer = ResolveLayer(root, out _);
            string old = layer.Name;
            layer.SetName(newName);
            return new { renamed = true, oldName = old, newName };
        }

        /// <summary>
        /// set_definition_query — convenience wrapper for the most common CIM edit.
        ///   {"command":"set_definition_query","layer":"X","query":"Stage = 3"}
        /// An empty/missing query clears the active definition query.
        /// </summary>
        private static object DoSetDefinitionQuery(JsonElement root)
        {
            var layer = ResolveLayer(root, out _) as FeatureLayer
                ?? throw new Exception("set_definition_query target must be a feature layer");
            string query = Str(root, "query") ?? "";

            if (string.IsNullOrWhiteSpace(query))
            {
                // Clear: drop the active definition query.
                if (layer.ActiveDefinitionQuery != null) layer.RemoveActiveDefinitionQuery();
                return new { layer = layer.Name, definitionQuery = (string)null, cleared = true };
            }

            // SetDefinitionQuery creates/updates a query for this where-clause and makes it active.
            layer.SetDefinitionQuery(query);
            return new { layer = layer.Name, definitionQuery = layer.DefinitionQuery };
        }

        /// <summary>
        /// set_visibility — toggle a layer on/off in the TOC.
        ///   {"command":"set_visibility","layer":"X","visible":false}
        /// </summary>
        private static object DoSetVisibility(JsonElement root)
        {
            bool visible = BoolOr(root, "visible", true);
            var layer = ResolveLayer(root, out _);
            layer.SetVisibility(visible);
            return new { layer = layer.Name, visible };
        }

        /// <summary>
        /// create_group — create a new (empty) group layer in a map.
        ///   {"command":"create_group","name":"Stages"[,"map":"M"][,"index":0]}
        /// </summary>
        private static object DoCreateGroup(JsonElement root)
        {
            string name = Str(root, "name");
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("missing 'name' (group layer name)");
            var map = ResolveMap(root);
            int index = IntOr(root, "index", 0);
            var grp = LayerFactory.Instance.CreateGroupLayer(map, index, name);
            return new { created = true, group = grp.Name, map = map.Name, index };
        }

        /// <summary>
        /// move_layer — reposition a layer in the TOC and/or move it into/out of a group.
        ///   {"command":"move_layer","layer":"X","index":0}                 // within current container
        ///   {"command":"move_layer","layer":"X","group":"Stages","index":0} // into a group
        /// group:"" (empty string) moves the layer back to the map root.
        /// </summary>
        private static object DoMoveLayer(JsonElement root)
        {
            var layer = ResolveLayer(root, out var map);
            int index = IntOr(root, "index", 0);
            bool hasGroup = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("group", out _);
            string groupName = Str(root, "group");

            if (hasGroup && !string.IsNullOrWhiteSpace(groupName))
            {
                var grp = map.GetLayersAsFlattenedList().OfType<GroupLayer>()
                    .FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception($"group layer not found: {groupName}");
                map.MoveLayer(layer, grp, index);
                return new { moved = true, layer = layer.Name, intoGroup = grp.Name, index };
            }

            // No group (or empty) -> move within / to the map root.
            map.MoveLayer(layer, index);
            return new { moved = true, layer = layer.Name, index, container = "map" };
        }

        /// <summary>
        /// clone_layer — duplicate an existing layer (full CIM: symbology + label classes +
        /// definition queries) one or more times into the same map (optionally into a group).
        ///   {"command":"clone_layer","layer":"Stage 3","name":"Stage 3 copy"}
        ///   {"command":"clone_layer","layer":"Stage 3","count":2}
        ///   {"command":"clone_layer","layer":"Stage 3","name":"S3","group":"Stages","index":0}
        /// </summary>
        private static object DoCloneLayer(JsonElement root)
        {
            var src = ResolveLayer(root, out var map);
            int count = IntOr(root, "count", 1);
            if (count < 1) count = 1;
            string baseName = Str(root, "name");
            int index = IntOr(root, "index", 0);

            // Optional destination group.
            ILayerContainerEdit container = map;
            string groupName = Str(root, "group");
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                container = map.GetLayersAsFlattenedList().OfType<GroupLayer>()
                    .FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception($"group layer not found: {groupName}");
            }

            // Snapshot the source's full CIM definition once; deep-copy per clone via JSON.
            var srcDef = src.GetDefinition();
            string srcJson = srcDef.ToJson(null);

            var made = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var def = (CIMBaseLayer)CIMObject.FromJson(srcJson, null);
                string newName = baseName ?? src.Name;
                if (count > 1) newName = $"{(baseName ?? src.Name)} {i + 1}";
                def.Name = newName;
                // New URI so the clone is a distinct layer, not an alias of the source.
                def.URI = "CIMPATH=" + Guid.NewGuid().ToString("N");

                var doc = new CIMLayerDocument
                {
                    Version = LayerDocVersion(),
                    Layers = new[] { def.URI },
                    LayerDefinitions = new CIMDefinition[] { def }
                };
                var prms = new LayerCreationParams(doc) { Name = newName, MapMemberIndex = index + i };
                var clone = LayerFactory.Instance.CreateLayer<FeatureLayer>(prms, container);
                made.Add(clone?.Name ?? newName);
            }

            return new { cloned = true, source = src.Name, count, layers = made };
        }

        /// <summary>
        /// set_layout_text — set the text of a named layout text element (e.g. "Title",
        /// "Title Description"). Works on graphic text and dynamic-text elements.
        ///   {"command":"set_layout_text","layout":"L","element":"Title","text":"New title"}
        /// </summary>
        private static object DoSetLayoutText(JsonElement root)
        {
            string text = Str(root, "text");
            if (text == null) throw new Exception("missing 'text'");
            var el = ResolveElement(root, out var layout);
            if (el is TextElement te)
            {
                var tp = te.TextProperties;
                tp.Text = text;
                te.SetTextProperties(tp);
            }
            else
            {
                // Fallback for non-TextElement graphics: edit the CIM text directly.
                // A text element's CIM is a CIMGraphicElement whose .Graphic is a CIMTextGraphic.
                var def = el.GetDefinition();
                if (def is CIMGraphicElement ge && ge.Graphic is CIMTextGraphic tg)
                {
                    tg.Text = text;
                    el.SetDefinition(def);
                }
                else throw new Exception($"element '{el.Name}' is not a text element ({el.GetType().Name})");
            }
            return new { layout = layout.Name, element = el.Name, text };
        }

        /// <summary>
        /// repoint_datasource — point a layer at a different feature class (e.g. a dissolved
        /// copy). Convenience wrapper over the GDB+name data-connection replace.
        ///   {"command":"repoint_datasource","layer":"X","gdb":"C:\\d.gdb","dataset":"d_dissolve"}
        /// </summary>
        private static object DoRepointDatasource(JsonElement root)
        {
            var layer = ResolveLayer(root, out _);
            string gdb = Str(root, "gdb");
            string dataset = Str(root, "dataset");
            if (string.IsNullOrWhiteSpace(gdb) || string.IsNullOrWhiteSpace(dataset))
                throw new Exception("repoint_datasource needs 'gdb' (file gdb path) and 'dataset' (fc name)");

            var conn = new CIMStandardDataConnection
            {
                WorkspaceConnectionString = $"DATABASE={gdb}",
                WorkspaceFactory = WorkspaceFactory.FileGDB,
                Dataset = dataset,
                DatasetType = esriDatasetType.esriDTFeatureClass
            };
            layer.SetDataConnection(conn, true);
            return new { layer = layer.Name, gdb, dataset, repointed = true };
        }

        // ---- resolvers / helpers ----------------------------------------------------

        private static Map ResolveMap(JsonElement root)
        {
            Map map = null;
            string mapName = Str(root, "map");
            if (!string.IsNullOrWhiteSpace(mapName))
                map = Project.Current.GetItems<MapProjectItem>()
                    .FirstOrDefault(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase))?.GetMap();
            map ??= MapView.Active?.Map;
            map ??= Project.Current.GetItems<MapProjectItem>().FirstOrDefault()?.GetMap();
            if (map == null) throw new Exception("工程里没有可用的地图 (no map available)");
            return map;
        }

        private static Layer ResolveLayer(JsonElement root, out Map map)
        {
            string layerName = Str(root, "layer");
            if (string.IsNullOrWhiteSpace(layerName)) throw new Exception("missing 'layer'");
            map = ResolveMap(root);
            var layer = map.GetLayersAsFlattenedList()
                .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
            if (layer == null) throw new Exception($"地图「{map.Name}」里找不到图层: {layerName}");
            return layer;
        }

        private static Layout ResolveLayout(JsonElement root)
        {
            string layoutName = Str(root, "layout");
            Layout layout = null;
            if (!string.IsNullOrWhiteSpace(layoutName))
            {
                var item = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(i => string.Equals(i.Name, layoutName, StringComparison.OrdinalIgnoreCase));
                layout = item?.GetLayout();
                if (layout == null) throw new Exception($"layout not found: {layoutName}");
            }
            layout ??= LayoutView.Active?.Layout;
            layout ??= Project.Current.GetItems<LayoutProjectItem>().FirstOrDefault()?.GetLayout();
            if (layout == null) throw new Exception("no layout available in this project");
            return layout;
        }

        private static Element ResolveElement(JsonElement root, out Layout layout)
        {
            layout = ResolveLayout(root);
            string elName = Str(root, "element");
            if (string.IsNullOrWhiteSpace(elName)) throw new Exception("missing 'element' (layout element name)");
            var el = layout.FindElement(elName);
            if (el == null) throw new Exception($"layout「{layout.Name}」里找不到元素: {elName}");
            return el;
        }

        // CIMLayerDocument version string for clones. Pro 3.x accepts the major series.
        private static string LayerDocVersion() => "3.0.0";

        private static bool BoolOr(JsonElement root, string name, bool dflt)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v)) return dflt;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
            return dflt;
        }
    }
}
