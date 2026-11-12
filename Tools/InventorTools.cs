using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Inventor;
using InventorMcpServer.Models;
using InventorMcpServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using ModelContextProtocol.Server;

namespace InventorMcpServer.Tools;

/// <summary>
/// Model Context Protocol (MCP) tools for Autodesk Inventor 2025.
/// All tool operations marshal onto the dedicated STA message pump.
/// </summary>
[McpServerToolType]
public sealed class InventorTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly StaThreadDispatcher _dispatcher;
    private readonly InventorSessionManager _sessionManager;
    private readonly EntityRegistry _entityRegistry;

    public InventorTools(
        StaThreadDispatcher dispatcher,
        InventorSessionManager sessionManager,
        EntityRegistry entityRegistry)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _entityRegistry = entityRegistry ?? throw new ArgumentNullException(nameof(entityRegistry));
    }

    #region 1. get_document_context

    [McpServerTool(Name = "get_document_context")]
    [Description("Inspects the active Autodesk Inventor document and returns structured JSON with document type, display name, file path, dirty state, active length/angle units, user parameters, and assembly occurrences or part solid bodies and features.")]
    public async Task<string> GetDocumentContext()
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            Inventor.Application app;
            try
            {
                app = _sessionManager.GetApplication();
            }
            catch (Exception ex)
            {
                return Serialize(new
                {
                    has_active_document = false,
                    error = ex.Message
                });
            }

            _Document? doc = SafeCall(() => app.ActiveDocument, null);

            if (doc == null)
            {
                return Serialize(new
                {
                    has_active_document = false,
                    inventor_version = app.SoftwareVersion.DisplayVersion,
                    message = "No document is currently active in Autodesk Inventor 2025."
                });
            }

            _entityRegistry.CheckActiveDocument(doc);

            string docTypeStr = doc.DocumentType switch
            {
                DocumentTypeEnum.kPartDocumentObject => "PartDocument",
                DocumentTypeEnum.kAssemblyDocumentObject => "AssemblyDocument",
                DocumentTypeEnum.kDrawingDocumentObject => "DrawingDocument",
                DocumentTypeEnum.kPresentationDocumentObject => "PresentationDocument",
                _ => doc.DocumentType.ToString()
            };

            var uom = doc.UnitsOfMeasure;
            string lengthDisplayUnit = SafeCall(() => uom.GetStringFromType(uom.LengthUnits), "Unknown");
            string angleDisplayUnit = SafeCall(() => uom.GetStringFromType(uom.AngleUnits), "Unknown");

            // Extract User Parameters
            var userParameters = new List<object>();
            Parameters? parameters = null;
            if (doc is PartDocument partDoc)
            {
                parameters = SafeCall(() => partDoc.ComponentDefinition.Parameters, null);
            }
            else if (doc is AssemblyDocument asmDoc)
            {
                parameters = SafeCall(() => asmDoc.ComponentDefinition.Parameters, null);
            }

            if (parameters != null)
            {
                foreach (UserParameter p in parameters.UserParameters)
                {
                    userParameters.Add(new
                    {
                        name = p.Name,
                        expression = p.Expression,
                        value = p.Value,
                        units = SafeCall(() => p.get_Units(), "Unknown"),
                        comment = p.Comment
                    });
                }
            }

            // Assembly Occurrences or Part Bodies & Features
            var occurrences = new List<object>();
            var solidBodies = new List<object>();
            var features = new List<object>();

            if (doc is AssemblyDocument assemblyDocument)
            {
                var compDef = assemblyDocument.ComponentDefinition;
                foreach (ComponentOccurrence occ in compDef.Occurrences)
                {
                    string handle = _entityRegistry.Register(occ, "occ", $"Occurrence: {occ.Name}", doc);
                    var translation = SafeCall(() =>
                    {
                        var t = occ.Transformation.Translation;
                        return new[] { t.X, t.Y, t.Z };
                    }, [0.0, 0.0, 0.0]);

                    occurrences.Add(new
                    {
                        handle,
                        name = occ.Name,
                        suppressed = occ.Suppressed,
                        grounded = occ.Grounded,
                        visible = occ.Visible,
                        translation
                    });
                }
            }
            else if (doc is PartDocument partDocument)
            {
                var compDef = partDocument.ComponentDefinition;
                foreach (SurfaceBody body in compDef.SurfaceBodies)
                {
                    string handle = _entityRegistry.Register(body, "body", $"Body: {body.Name}", doc);
                    var rangeBox = SafeCall(() => body.RangeBox, null);

                    solidBodies.Add(new
                    {
                        handle,
                        name = body.Name,
                        visible = body.Visible,
                        face_count = SafeCall(() => body.Faces.Count, 0),
                        edge_count = SafeCall(() => body.Edges.Count, 0),
                        bounding_box = rangeBox == null ? null : new
                        {
                            min = new[] { rangeBox.MinPoint.X, rangeBox.MinPoint.Y, rangeBox.MinPoint.Z },
                            max = new[] { rangeBox.MaxPoint.X, rangeBox.MaxPoint.Y, rangeBox.MaxPoint.Z }
                        }
                    });
                }

                foreach (PartFeature feat in compDef.Features)
                {
                    features.Add(new
                    {
                        name = feat.Name,
                        type = feat.Type.ToString(),
                        suppressed = feat.Suppressed,
                        health_status = feat.HealthStatus.ToString()
                    });
                }
            }

            return Serialize(new
            {
                has_active_document = true,
                inventor_version = app.SoftwareVersion.DisplayVersion,
                document_type = docTypeStr,
                display_name = doc.DisplayName,
                full_file_name = string.IsNullOrEmpty(doc.FullFileName) ? "[Unsaved]" : doc.FullFileName,
                is_dirty = doc.Dirty,
                units = new
                {
                    length_display_units = lengthDisplayUnit,
                    angle_display_units = angleDisplayUnit,
                    api_internal_units_note = "Inventor API internal units are strictly centimeters (cm) for length and radians (rad) for angle. Display units are shown for reference."
                },
                user_parameters = userParameters,
                occurrences = doc is AssemblyDocument ? occurrences : null,
                solid_bodies = doc is PartDocument ? solidBodies : null,
                features = doc is PartDocument ? features : null
            });
        });
    }

    #endregion

    #region 2. reflect_inventor_api

    [McpServerTool(Name = "reflect_inventor_api")]
    [Description("Reflects on Autodesk Inventor 2025 API types from Autodesk.Inventor.Interop.dll. Returns clean, token-efficient method signatures, property declarations, or enum values for exact API discovery.")]
    public async Task<string> ReflectInventorApi(
        [Description("Name or partial name of the Inventor API type (e.g. 'PartDocument', 'ExtrudeFeature', 'UnitsTypeEnum', 'Face', 'Application').")]
        string typeName,
        [Description("Optional case-insensitive filter substring for property, method, or enum member names.")]
        string? memberFilter = null,
        [Description("If true, includes inherited interface/base members; defaults to false for concise signatures.")]
        bool includeInherited = false)
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return Serialize(new { error = "Parameter 'typeName' must not be empty." });
            }

            var asm = typeof(Inventor.Application).Assembly;
            var cleanTypeName = typeName.Trim();

            // Find matching type
            var targetType = FindType(asm, cleanTypeName);

            if (targetType == null)
            {
                // Find candidates
                var candidates = asm.GetTypes()
                    .Where(t => t.IsPublic && t.Name.Contains(cleanTypeName, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.Name)
                    .Take(25)
                    .ToList();

                return Serialize(new
                {
                    error = $"Type '{cleanTypeName}' not found in Autodesk.Inventor.Interop assembly.",
                    suggested_matches = candidates
                });
            }

            if (targetType.IsEnum)
            {
                var names = Enum.GetNames(targetType);
                var values = new List<object>();
                foreach (var name in names)
                {
                    if (string.IsNullOrEmpty(memberFilter) || name.Contains(memberFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        var val = Convert.ToInt64(Enum.Parse(targetType, name));
                        values.Add(new { name, value = val });
                    }
                }

                return Serialize(new
                {
                    type_name = targetType.FullName,
                    kind = "Enum",
                    values_count = values.Count,
                    values
                });
            }

            // Interface or Class
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance;
            if (!includeInherited)
            {
                bindingFlags |= BindingFlags.DeclaredOnly;
            }

            // Gather all types to reflect (including inherited interfaces if requested)
            var typesToScan = new List<Type> { targetType };
            if (includeInherited && targetType.IsInterface)
            {
                typesToScan.AddRange(targetType.GetInterfaces());
            }

            var propertyList = new List<string>();
            var methodList = new List<string>();
            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            var seenMethods = new HashSet<string>(StringComparer.Ordinal);

            foreach (var typeToScan in typesToScan)
            {
                // Properties
                foreach (var prop in typeToScan.GetProperties(bindingFlags))
                {
                    if (!seenProperties.Add(prop.Name)) continue;

                    if (!string.IsNullOrEmpty(memberFilter) &&
                        !prop.Name.Contains(memberFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string getSet = (prop.CanRead && prop.CanWrite) ? "{ get; set; }"
                        : prop.CanRead ? "{ get; }"
                        : "{ set; }";

                    propertyList.Add($"{FormatTypeName(prop.PropertyType)} {prop.Name} {getSet}");
                }

                // Methods
                foreach (var method in typeToScan.GetMethods(bindingFlags))
                {
                    if (method.IsSpecialName) continue; // Skip property getters/setters/events

                    if (!string.IsNullOrEmpty(memberFilter) &&
                        !method.Name.Contains(memberFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var paramStrs = method.GetParameters().Select(p =>
                    {
                        string modifier = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "";
                        var pType = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
                        string defaultVal = p.HasDefaultValue ? $" = {(p.DefaultValue == null ? "null" : p.DefaultValue)}" : "";
                        return $"{modifier}{FormatTypeName(pType)} {p.Name}{defaultVal}";
                    });

                    string signature = $"{FormatTypeName(method.ReturnType)} {method.Name}({string.Join(", ", paramStrs)})";
                    if (seenMethods.Add(signature))
                    {
                        methodList.Add(signature);
                    }
                }
            }

            return Serialize(new
            {
                type_name = targetType.FullName,
                kind = targetType.IsInterface ? "Interface" : "Class",
                filter = memberFilter,
                include_inherited = includeInherited,
                properties_count = propertyList.Count,
                properties = propertyList.OrderBy(p => p).ToList(),
                methods_count = methodList.Count,
                methods = methodList.OrderBy(m => m).ToList()
            });
        });
    }

    #endregion

    #region 3. query_geometry

    [McpServerTool(Name = "query_geometry")]
    [Description("Resolves semantic geometric queries in the active Inventor document (planar_faces, cylindrical_faces, edges, occurrences, attributes). Registers matched COM entities in the EntityRegistry and returns short handles with geometric summaries (centroid, normal, area/length, bounding box in cm).")]
    public async Task<string> QueryGeometry(
        [Description("Query type: 'planar_faces', 'cylindrical_faces', 'edges', 'occurrences', or 'attributes'.")]
        string queryType,
        [Description("Optional JSON string specifying filter criteria (e.g. normal: [0,0,1], min_area: 10.0, axis: [0,1,0], min_radius: 1.5, name: 'Bolt*', set_name: 'CustomSet').")]
        string? filterJson = null)
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            Inventor.Application app;
            try
            {
                app = _sessionManager.GetApplication();
            }
            catch (Exception ex)
            {
                return Serialize(new
                {
                    has_active_document = false,
                    error = ex.Message
                });
            }

            _Document? doc = SafeCall(() => app.ActiveDocument, null);

            if (doc == null)
            {
                return Serialize(new
                {
                    error = "No document is currently active in Autodesk Inventor 2025. Please open a part or assembly before querying geometry."
                });
            }

            _entityRegistry.CheckActiveDocument(doc);

            GeometryFilterOptions filter = new();
            if (!string.IsNullOrWhiteSpace(filterJson))
            {
                try
                {
                    filter = JsonSerializer.Deserialize<GeometryFilterOptions>(filterJson, JsonOptions) ?? new();
                }
                catch (Exception ex)
                {
                    return Serialize(new
                    {
                        error = $"Invalid filterJson syntax: {ex.Message}",
                        filter_json_received = filterJson
                    });
                }
            }

            var cleanQueryType = queryType?.Trim().ToLowerInvariant() ?? string.Empty;

            return cleanQueryType switch
            {
                "planar_faces" or "planar_face" or "plane" => QueryPlanarFaces(doc, filter),
                "cylindrical_faces" or "cylindrical_face" or "cylinder" => QueryCylindricalFaces(doc, filter),
                "edges" or "edge" => QueryEdges(doc, filter),
                "occurrences" or "occurrence" => QueryOccurrences(doc, filter),
                "attributes" or "attribute" => QueryAttributes(doc, filter),
                _ => Serialize(new
                {
                    error = $"Unsupported queryType: '{queryType}'. Supported types are: 'planar_faces', 'cylindrical_faces', 'edges', 'occurrences', 'attributes'."
                })
            };
        });
    }

    private string QueryPlanarFaces(_Document doc, GeometryFilterOptions filter)
    {
        var faces = GetAllFaces(doc, filter.BodyName);
        var results = new List<object>();

        foreach (Face face in faces)
        {
            if (results.Count >= filter.MaxResults) break;

            if (face.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;

            if (face.Geometry is not Plane plane) continue;

            var normal = plane.Normal;
            double area = SafeCall(() => face.Evaluator.Area, 0.0);

            // Filter by Normal vector
            if (filter.Normal != null && filter.Normal.Length == 3)
            {
                var target = Normalize(filter.Normal);
                double dot = (normal.X * target[0]) + (normal.Y * target[1]) + (normal.Z * target[2]);
                if (Math.Abs(dot - 1.0) > filter.Tolerance)
                {
                    continue;
                }
            }

            // Filter by Area range
            if (filter.MinArea.HasValue && area < filter.MinArea.Value) continue;
            if (filter.MaxArea.HasValue && area > filter.MaxArea.Value) continue;

            string handle = _entityRegistry.Register(face, "face", $"Planar Face on body {SafeCall(() => face.Parent.Name, "unknown")}", doc);
            var pt = SafeCall(() => face.PointOnFace, null);
            var rangeBox = SafeCall(() => face.Evaluator.RangeBox, null);

            results.Add(new
            {
                handle,
                surface_type = "PlanarFace",
                normal = new[] { Math.Round(normal.X, 6), Math.Round(normal.Y, 6), Math.Round(normal.Z, 6) },
                point_on_face = pt == null ? null : new[] { Math.Round(pt.X, 4), Math.Round(pt.Y, 4), Math.Round(pt.Z, 4) },
                area_cm2 = Math.Round(area, 4),
                bounding_box = rangeBox == null ? null : new
                {
                    min = new[] { Math.Round(rangeBox.MinPoint.X, 4), Math.Round(rangeBox.MinPoint.Y, 4), Math.Round(rangeBox.MinPoint.Z, 4) },
                    max = new[] { Math.Round(rangeBox.MaxPoint.X, 4), Math.Round(rangeBox.MaxPoint.Y, 4), Math.Round(rangeBox.MaxPoint.Z, 4) }
                }
            });
        }

        return Serialize(new
        {
            query_type = "planar_faces",
            matched_count = results.Count,
            units = "Centimeters (cm), Area in cm^2",
            entities = results
        });
    }

    private string QueryCylindricalFaces(_Document doc, GeometryFilterOptions filter)
    {
        var faces = GetAllFaces(doc, filter.BodyName);
        var results = new List<object>();

        foreach (Face face in faces)
        {
            if (results.Count >= filter.MaxResults) break;

            if (face.SurfaceType != SurfaceTypeEnum.kCylinderSurface) continue;

            if (face.Geometry is not Cylinder cyl) continue;

            double radius = cyl.Radius;
            var axis = cyl.AxisVector;
            double area = SafeCall(() => face.Evaluator.Area, 0.0);

            // Filter by Radius range
            if (filter.MinRadius.HasValue && radius < filter.MinRadius.Value) continue;
            if (filter.MaxRadius.HasValue && radius > filter.MaxRadius.Value) continue;

            // Filter by Area range
            if (filter.MinArea.HasValue && area < filter.MinArea.Value) continue;
            if (filter.MaxArea.HasValue && area > filter.MaxArea.Value) continue;

            // Filter by Axis vector
            if (filter.Axis != null && filter.Axis.Length == 3)
            {
                var target = Normalize(filter.Axis);
                double dot = (axis.X * target[0]) + (axis.Y * target[1]) + (axis.Z * target[2]);
                if (Math.Abs(Math.Abs(dot) - 1.0) > filter.Tolerance)
                {
                    continue;
                }
            }

            string handle = _entityRegistry.Register(face, "cyl_face", $"Cylindrical Face (R={radius:F3}cm)", doc);
            var basePt = cyl.BasePoint;
            var rangeBox = SafeCall(() => face.Evaluator.RangeBox, null);

            results.Add(new
            {
                handle,
                surface_type = "CylindricalFace",
                radius_cm = Math.Round(radius, 4),
                axis_vector = new[] { Math.Round(axis.X, 6), Math.Round(axis.Y, 6), Math.Round(axis.Z, 6) },
                base_point = new[] { Math.Round(basePt.X, 4), Math.Round(basePt.Y, 4), Math.Round(basePt.Z, 4) },
                area_cm2 = Math.Round(area, 4),
                bounding_box = rangeBox == null ? null : new
                {
                    min = new[] { Math.Round(rangeBox.MinPoint.X, 4), Math.Round(rangeBox.MinPoint.Y, 4), Math.Round(rangeBox.MinPoint.Z, 4) },
                    max = new[] { Math.Round(rangeBox.MaxPoint.X, 4), Math.Round(rangeBox.MaxPoint.Y, 4), Math.Round(rangeBox.MaxPoint.Z, 4) }
                }
            });
        }

        return Serialize(new
        {
            query_type = "cylindrical_faces",
            matched_count = results.Count,
            units = "Centimeters (cm), Area in cm^2",
            entities = results
        });
    }

    private string QueryEdges(_Document doc, GeometryFilterOptions filter)
    {
        var edges = GetAllEdges(doc, filter.BodyName);
        var results = new List<object>();

        foreach (Edge edge in edges)
        {
            if (results.Count >= filter.MaxResults) break;

            double length = 0.0;
            try
            {
                edge.Evaluator.GetParamExtents(out double minParam, out double maxParam);
                edge.Evaluator.GetLengthAtParam(minParam, maxParam, out length);
            }
            catch
            {
                // In case evaluator fails
            }

            // Filter by Length
            if (filter.MinLength.HasValue && length < filter.MinLength.Value) continue;
            if (filter.MaxLength.HasValue && length > filter.MaxLength.Value) continue;

            // Filter by Curve Type
            string curveTypeStr = edge.CurveType.ToString();
            if (!string.IsNullOrEmpty(filter.CurveType) &&
                !curveTypeStr.Contains(filter.CurveType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Circle / Arc radius filter
            double? radius = null;
            if (edge.Geometry is Circle circle)
            {
                radius = circle.Radius;
            }
            else if (edge.Geometry is Arc3d arc)
            {
                radius = arc.Radius;
            }

            if (filter.MinRadius.HasValue && (radius == null || radius.Value < filter.MinRadius.Value)) continue;
            if (filter.MaxRadius.HasValue && (radius == null || radius.Value > filter.MaxRadius.Value)) continue;

            string handle = _entityRegistry.Register(edge, "edge", $"Edge ({curveTypeStr}, L={length:F3}cm)", doc);
            var startPt = SafeCall(() => edge.StartVertex?.Point, null);
            var stopPt = SafeCall(() => edge.StopVertex?.Point, null);

            results.Add(new
            {
                handle,
                curve_type = curveTypeStr,
                length_cm = Math.Round(length, 4),
                radius_cm = radius.HasValue ? Math.Round(radius.Value, 4) : (double?)null,
                start_point = startPt == null ? null : new[] { Math.Round(startPt.X, 4), Math.Round(startPt.Y, 4), Math.Round(startPt.Z, 4) },
                stop_point = stopPt == null ? null : new[] { Math.Round(stopPt.X, 4), Math.Round(stopPt.Y, 4), Math.Round(stopPt.Z, 4) }
            });
        }

        return Serialize(new
        {
            query_type = "edges",
            matched_count = results.Count,
            units = "Centimeters (cm)",
            entities = results
        });
    }

    private string QueryOccurrences(_Document doc, GeometryFilterOptions filter)
    {
        if (doc is not AssemblyDocument asmDoc)
        {
            return Serialize(new
            {
                error = "queryType 'occurrences' is only valid for an active AssemblyDocument.",
                current_document_type = doc.DocumentType.ToString()
            });
        }

        var results = new List<object>();
        var occurrences = asmDoc.ComponentDefinition.Occurrences;

        foreach (ComponentOccurrence occ in occurrences)
        {
            if (results.Count >= filter.MaxResults) break;

            if (!string.IsNullOrEmpty(filter.Name) &&
                !occ.Name.Contains(filter.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filter.Grounded.HasValue && occ.Grounded != filter.Grounded.Value) continue;
            if (filter.Suppressed.HasValue && occ.Suppressed != filter.Suppressed.Value) continue;

            string handle = _entityRegistry.Register(occ, "occ", $"Occurrence: {occ.Name}", doc);
            var translation = SafeCall(() =>
            {
                var t = occ.Transformation.Translation;
                return new[] { Math.Round(t.X, 4), Math.Round(t.Y, 4), Math.Round(t.Z, 4) };
            }, [0.0, 0.0, 0.0]);

            var rangeBox = SafeCall(() => occ.RangeBox, null);

            results.Add(new
            {
                handle,
                name = occ.Name,
                suppressed = occ.Suppressed,
                grounded = occ.Grounded,
                visible = occ.Visible,
                translation,
                bounding_box = rangeBox == null ? null : new
                {
                    min = new[] { Math.Round(rangeBox.MinPoint.X, 4), Math.Round(rangeBox.MinPoint.Y, 4), Math.Round(rangeBox.MinPoint.Z, 4) },
                    max = new[] { Math.Round(rangeBox.MaxPoint.X, 4), Math.Round(rangeBox.MaxPoint.Y, 4), Math.Round(rangeBox.MaxPoint.Z, 4) }
                }
            });
        }

        return Serialize(new
        {
            query_type = "occurrences",
            matched_count = results.Count,
            entities = results
        });
    }

    private string QueryAttributes(_Document doc, GeometryFilterOptions filter)
    {
        var results = new List<object>();

        // Query attributes on faces, bodies, and occurrences
        void CheckAttributeSets(object entity, string typeDesc, string prefix)
        {
            if (results.Count >= filter.MaxResults) return;

            try
            {
                var prop = entity.GetType().GetProperty("AttributeSets");
                if (prop?.GetValue(entity) is AttributeSets attributeSets && attributeSets.Count > 0)
                {
                    var matchedSets = new Dictionary<string, Dictionary<string, object>>();
                    bool matchesFilter = false;

                    foreach (AttributeSet set in attributeSets)
                    {
                        if (!string.IsNullOrEmpty(filter.SetName) &&
                            !set.Name.Contains(filter.SetName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var attrs = new Dictionary<string, object>();
                        foreach (Inventor.Attribute attr in set)
                        {
                            if (!string.IsNullOrEmpty(filter.AttributeName) &&
                                !attr.Name.Contains(filter.AttributeName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(filter.AttributeValue) &&
                                !attr.Value.ToString()!.Contains(filter.AttributeValue, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            attrs[attr.Name] = attr.Value;
                            matchesFilter = true;
                        }

                        if (attrs.Count > 0)
                        {
                            matchedSets[set.Name] = attrs;
                        }
                    }

                    if (matchesFilter || (string.IsNullOrEmpty(filter.SetName) && string.IsNullOrEmpty(filter.AttributeName) && matchedSets.Count > 0))
                    {
                        string handle = _entityRegistry.Register(entity, prefix, $"{typeDesc} with attributes", doc);
                        results.Add(new
                        {
                            handle,
                            entity_type = typeDesc,
                            attribute_sets = matchedSets
                        });
                    }
                }
            }
            catch
            {
                // Ignore COM inspection errors on individual entities
            }
        }

        if (doc is AssemblyDocument asmDoc)
        {
            foreach (ComponentOccurrence occ in asmDoc.ComponentDefinition.Occurrences)
            {
                CheckAttributeSets(occ, "ComponentOccurrence", "occ");
            }
        }
        else if (doc is PartDocument partDoc)
        {
            foreach (SurfaceBody body in partDoc.ComponentDefinition.SurfaceBodies)
            {
                CheckAttributeSets(body, "SurfaceBody", "body");
                foreach (Face face in body.Faces)
                {
                    CheckAttributeSets(face, "Face", "face");
                }
            }
        }

        return Serialize(new
        {
            query_type = "attributes",
            matched_count = results.Count,
            entities = results
        });
    }

    #endregion

    #region 4. execute_inventor_csharp

    [McpServerTool(Name = "execute_inventor_csharp")]
    [Description("Compiles and executes C# code against Autodesk Inventor 2025 in a transaction on the dedicated STA thread. Exposes App, Doc, TG (TransientGeometry), GetEntity(handle), RegisterEntity(obj), and Log(msg). Automatically commits or aborts the transaction on error.")]
    public async Task<string> ExecuteInventorCSharp(
        [Description("C# script or snippet to execute. Has access to App, Doc, TG, GetEntity, RegisterEntity, and Log.")]
        string csharpCode,
        [Description("If true, wraps execution in an Inventor Undo Transaction and aborts automatically on error. Defaults to true.")]
        bool wrapInTransaction = true,
        [Description("Display description for the undo transaction. Defaults to 'MCP Agent Action'.")]
        string transactionDescription = "MCP Agent Action")
    {
        return await _dispatcher.InvokeAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var logs = new List<string>();
            Transaction? txn = null;
            _Document? doc = null;

            // 1. Script options with pre-referenced assemblies and imports
            var scriptOptions = ScriptOptions.Default
                .AddReferences(
                    typeof(object).Assembly,
                    typeof(Enumerable).Assembly,
                    typeof(List<>).Assembly,
                    typeof(Inventor.Application).Assembly,
                    typeof(ScriptGlobals).Assembly,
                    typeof(JsonSerializer).Assembly
                )
                .AddImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.Text",
                    "Inventor",
                    "InventorMcpServer.Models"
                );

            // 2. Compile upfront to detect syntax and compilation errors with rich line/character diagnostics
            Script<object> script;
            try
            {
                script = CSharpScript.Create<object>(csharpCode, scriptOptions, typeof(ScriptGlobals));
                var diagnostics = script.Compile();
                var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (errors.Count > 0)
                {
                    sw.Stop();
                    var diagList = errors.Select(d => new
                    {
                        id = d.Id,
                        severity = d.Severity.ToString(),
                        message = d.GetMessage(),
                        line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                        character = d.Location.GetLineSpan().StartLinePosition.Character + 1
                    }).ToList();

                    return Serialize(new
                    {
                        success = false,
                        error_type = "CompilationError",
                        execution_time_ms = sw.ElapsedMilliseconds,
                        transaction_aborted = false,
                        message = "C# script failed to compile.",
                        diagnostics = diagList,
                        logs
                    });
                }
            }
            catch (CompilationErrorException cex)
            {
                sw.Stop();
                var diagList = cex.Diagnostics.Select(d => new
                {
                    id = d.Id,
                    severity = d.Severity.ToString(),
                    message = d.GetMessage(),
                    line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                    character = d.Location.GetLineSpan().StartLinePosition.Character + 1
                }).ToList();

                return Serialize(new
                {
                    success = false,
                    error_type = "CompilationError",
                    execution_time_ms = sw.ElapsedMilliseconds,
                    transaction_aborted = false,
                    message = "C# script failed to compile.",
                    diagnostics = diagList,
                    logs
                });
            }

            // 3. Attach to Inventor and execute
            try
            {
                var app = _sessionManager.GetApplication();
                doc = SafeCall(() => app.ActiveDocument, null);
                if (doc != null)
                {
                    _entityRegistry.CheckActiveDocument(doc);
                }

                var globals = new ScriptGlobals
                {
                    App = app,
                    Doc = doc as Inventor.Document,
                    TG = app.TransientGeometry,
                    GetEntity = handle => _entityRegistry.Get(handle),
                    RegisterEntity = obj => _entityRegistry.Register(obj, "script_ent", "Entity registered from C# script", doc),
                    Log = msg => logs.Add(msg ?? string.Empty)
                };

                if (wrapInTransaction && doc != null)
                {
                    try
                    {
                        txn = app.TransactionManager.StartTransaction(doc, transactionDescription);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[execute_inventor_csharp] Failed to start transaction: {ex.Message}");
                    }
                }

                var scriptState = await script.RunAsync(globals);
                var rawResult = scriptState.ReturnValue;

                if (txn != null)
                {
                    txn.End();
                    txn = null;
                }

                sw.Stop();
                object? serializedResult = SafeSerializeReturnValue(rawResult, doc);

                return Serialize(new
                {
                    success = true,
                    execution_time_ms = sw.ElapsedMilliseconds,
                    transaction_committed = wrapInTransaction && doc != null,
                    return_value = serializedResult,
                    logs
                });
            }
            catch (CompilationErrorException cex)
            {
                if (txn != null)
                {
                    try { txn.Abort(); } catch { }
                    txn = null;
                }

                sw.Stop();
                var diagnostics = cex.Diagnostics.Select(d => new
                {
                    id = d.Id,
                    severity = d.Severity.ToString(),
                    message = d.GetMessage(),
                    line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                    character = d.Location.GetLineSpan().StartLinePosition.Character + 1
                }).ToList();

                return Serialize(new
                {
                    success = false,
                    error_type = "CompilationError",
                    execution_time_ms = sw.ElapsedMilliseconds,
                    transaction_aborted = wrapInTransaction && doc != null,
                    message = "C# script failed to compile.",
                    diagnostics,
                    logs
                });
            }
            catch (Exception ex)
            {
                if (txn != null)
                {
                    try { txn.Abort(); } catch { }
                    txn = null;
                }

                sw.Stop();
                string hresultHex = $"0x{ex.HResult:X8}";

                return Serialize(new
                {
                    success = false,
                    error_type = "RuntimeError",
                    exception_type = ex.GetType().FullName,
                    hresult = hresultHex,
                    message = ex.Message,
                    execution_time_ms = sw.ElapsedMilliseconds,
                    transaction_aborted = wrapInTransaction && doc != null,
                    stack_trace = ex.StackTrace,
                    logs
                });
            }
        });
    }

    #endregion

    #region Helpers

    private object? SafeSerializeReturnValue(object? value, _Document? doc)
    {
        if (value == null) return null;

        // Primitives, strings, numbers, booleans, DateTimes
        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or Guid)
        {
            return value;
        }

        // COM Object: auto-register in EntityRegistry and return handle + type
        if (Marshal.IsComObject(value) || value.GetType().FullName?.StartsWith("Inventor.") == true)
        {
            string handle = _entityRegistry.Register(value, "result_ent", "Result object from C# script", doc);
            return new
            {
                is_com_object = true,
                handle,
                com_type = value.GetType().Name
            };
        }

        // Array or List of primitives
        if (value is IEnumerable enumerable and not IDictionary)
        {
            var list = new List<object?>();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ > 100)
                {
                    list.Add("... [truncated]");
                    break;
                }
                list.Add(SafeSerializeReturnValue(item, doc));
            }
            return list;
        }

        // Try standard JSON serialization
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return value.ToString();
        }
    }

    private static Type? FindType(Assembly asm, string typeName)
    {
        // 1. Exact match
        var t = asm.GetType(typeName, false, true);
        if (t != null) return t;

        // 2. Prefixed with Inventor.
        if (!typeName.StartsWith("Inventor.", StringComparison.OrdinalIgnoreCase))
        {
            t = asm.GetType($"Inventor.{typeName}", false, true);
            if (t != null) return t;
        }

        // 3. Name match across exported types
        t = asm.GetTypes().FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase));
        if (t != null) return t;

        return null;
    }

    private static string FormatTypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(double)) return "double";
        if (type == typeof(object)) return "object";

        if (type.IsGenericType)
        {
            string genericBase = type.Name[..type.Name.IndexOf('`')];
            var args = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{genericBase}<{args}>";
        }

        return type.Name;
    }

    private static double[] Normalize(double[] vec)
    {
        double mag = Math.Sqrt((vec[0] * vec[0]) + (vec[1] * vec[1]) + (vec[2] * vec[2]));
        if (mag < 1e-9) return [0, 0, 1];
        return [vec[0] / mag, vec[1] / mag, vec[2] / mag];
    }

    private static List<Face> GetAllFaces(_Document doc, string? bodyNameFilter)
    {
        var list = new List<Face>();
        var bodies = new List<SurfaceBody>();

        if (doc is PartDocument partDoc)
        {
            foreach (SurfaceBody b in partDoc.ComponentDefinition.SurfaceBodies)
            {
                bodies.Add(b);
            }
        }
        else if (doc is AssemblyDocument asmDoc)
        {
            foreach (ComponentOccurrence occ in asmDoc.ComponentDefinition.Occurrences)
            {
                if (occ.Suppressed) continue;
                foreach (SurfaceBody b in occ.SurfaceBodies)
                {
                    bodies.Add(b);
                }
            }
        }

        foreach (var body in bodies)
        {
            if (!string.IsNullOrEmpty(bodyNameFilter) &&
                !body.Name.Contains(bodyNameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Face face in body.Faces)
            {
                list.Add(face);
            }
        }

        return list;
    }

    private static List<Edge> GetAllEdges(_Document doc, string? bodyNameFilter)
    {
        var list = new List<Edge>();
        var bodies = new List<SurfaceBody>();

        if (doc is PartDocument partDoc)
        {
            foreach (SurfaceBody b in partDoc.ComponentDefinition.SurfaceBodies)
            {
                bodies.Add(b);
            }
        }
        else if (doc is AssemblyDocument asmDoc)
        {
            foreach (ComponentOccurrence occ in asmDoc.ComponentDefinition.Occurrences)
            {
                if (occ.Suppressed) continue;
                foreach (SurfaceBody b in occ.SurfaceBodies)
                {
                    bodies.Add(b);
                }
            }
        }

        foreach (var body in bodies)
        {
            if (!string.IsNullOrEmpty(bodyNameFilter) &&
                !body.Name.Contains(bodyNameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Edge edge in body.Edges)
            {
                list.Add(edge);
            }
        }

        return list;
    }

    private static T SafeCall<T>(Func<T> func, T fallback)
    {
        try
        {
            return func();
        }
        catch
        {
            return fallback;
        }
    }

    private static string Serialize(object obj)
    {
        return JsonSerializer.Serialize(obj, JsonOptions);
    }

    #endregion
}
