using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Massive;
using Massive.QoL;
using Massive.Unity;

namespace Massive.Unity.Editor
{
	/// <summary>
	/// Live ECS state inspector for Massive worlds.
	/// Displays all worlds, their entities, and component data in real-time.
	/// Menu: Window > Massive > State Inspector
	/// </summary>
	public class MassiveStateInspector : EditorWindow
	{
		// ── Constants ──────────────────────────────────────────────
		private const float MinPaneWidth = 200f;
		private const float SplitterWidth = 4f;
		private const float ToolbarHeight = 22f;
		private const float ThumbnailSize = 16f;
		private const float ThumbnailSpacing = 2f;
		private const float RowHeight = 22f;
		private const float RepaintInterval = 0.25f;
		private const string PrefSplitPos = "MassiveInspector_SplitPos";
		private const string PrefWorldName = "MassiveInspector_WorldName";

		// ── Colors ─────────────────────────────────────────────────
		private static readonly Color RowEvenColor = new Color(0, 0, 0, 0.04f);
		private static readonly Color RowOddColor = new Color(0, 0, 0, 0);
		private static readonly Color RowSelectedColor = new Color(0.17f, 0.36f, 0.53f, 1f);
		private static readonly Color RowHoverColor = new Color(0.3f, 0.3f, 0.3f, 0.2f);
		private static readonly Color SplitterColor = new Color(0.12f, 0.12f, 0.12f, 1f);
		private static readonly Color HeaderBgColor = new Color(0.22f, 0.22f, 0.22f, 1f);

		// ── State ──────────────────────────────────────────────────
		private int _selectedWorldIndex;
		private int _selectedEntityId = -1;
		private bool _liveUpdate = true;
		private string _searchFilter = "";
		private float _splitPosition = 350f;
		private bool _isDraggingSplitter;
		private int _keyboardFocusIndex = -1;

		// ── Scroll positions ───────────────────────────────────────
		private Vector2 _entityListScroll;
		private Vector2 _inspectorScroll;

		// ── Cached data ────────────────────────────────────────────
		private string[] _worldNames = Array.Empty<string>();
		private List<World> _worldLookup = new List<World>();
		private World _currentWorld;
		private readonly List<EntityEntry> _entities = new List<EntityEntry>();
		private readonly List<int> _filteredIndices = new List<int>();
		private readonly List<ComponentEntry> _selectedComponents = new List<ComponentEntry>();
		private readonly Dictionary<Type, Color> _componentColors = new Dictionary<Type, Color>();
		private double _lastRepaintTime;

		// ── Component filter ───────────────────────────────────────
		private readonly List<ComponentTypeInfo> _allComponentTypes = new List<ComponentTypeInfo>();
		private readonly HashSet<int> _filterComponentIds = new HashSet<int>();
		private bool _filterEnabled;

		// ── Foldout state ──────────────────────────────────────────
		private readonly HashSet<string> _expandedComponents = new HashSet<string>();
		private readonly HashSet<string> _expandedFields = new HashSet<string>();

		// ── Scene sync ─────────────────────────────────────────────
		private bool _sceneSync = true;

		// ── Styles (lazy init) ─────────────────────────────────────
		private static GUIStyle s_thumbnailStyle;
		private static GUIStyle s_entityRowStyle;
		private static GUIStyle s_headerStyle;
		private static GUIStyle s_componentHeaderStyle;
		private static GUIStyle s_fieldLabelStyle;
		private static GUIStyle s_fieldValueStyle;
		private static GUIStyle s_statsStyle;
		private static GUIStyle s_columnHeaderStyle;

		// ── Data types ─────────────────────────────────────────────
		private struct EntityEntry
		{
			public int Id;
			public uint Version;
			public string TypeLabel; // from EntityTypeTag if present
			public List<ComponentTypeInfo> Components;
		}

		private struct ComponentEntry
		{
			public Type Type;
			public string Name;
			public int ComponentId;
			public object Data;
			public FieldInfo[] Fields;
		}

		private struct ComponentTypeInfo
		{
			public Type Type;
			public string Name;
			public string ShortName;
			public int ComponentId;
			public Color Color;
		}

		// ════════════════════════════════════════════════════════════
		// Menu
		// ════════════════════════════════════════════════════════════

		[MenuItem("Window/Massive/State Inspector")]
		public static void Open()
		{
			var window = GetWindow<MassiveStateInspector>();
			window.titleContent = new GUIContent("Massive State", EditorGUIUtility.IconContent("d_UnityEditor.HierarchyWindow").image);
			window.minSize = new Vector2(500, 300);
			window.Show();
		}

		// ════════════════════════════════════════════════════════════
		// Lifecycle
		// ════════════════════════════════════════════════════════════

		private void OnEnable()
		{
			EditorApplication.playModeStateChanged += OnPlayModeChanged;
			Selection.selectionChanged += OnSceneSelectionChanged;
			_splitPosition = EditorPrefs.GetFloat(PrefSplitPos, 350f);
		}

		private void OnDisable()
		{
			EditorApplication.playModeStateChanged -= OnPlayModeChanged;
			Selection.selectionChanged -= OnSceneSelectionChanged;
			EditorPrefs.SetFloat(PrefSplitPos, _splitPosition);
		}

		private void OnPlayModeChanged(PlayModeStateChange change)
		{
			if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.ExitingPlayMode)
			{
				_selectedEntityId = -1;
				_keyboardFocusIndex = -1;
				_entities.Clear();
				_filteredIndices.Clear();
				_selectedComponents.Clear();
				_allComponentTypes.Clear();
				_componentColors.Clear();
				_worldLookup.Clear();
			}
			if (change == PlayModeStateChange.ExitingPlayMode)
			{
				MassiveWorldRegistry.Clear();
			}
			Repaint();
		}

		private void Update()
		{
			if (_liveUpdate && EditorApplication.isPlaying)
			{
				if (EditorApplication.timeSinceStartup - _lastRepaintTime > RepaintInterval)
				{
					_lastRepaintTime = EditorApplication.timeSinceStartup;
					Repaint();
				}
			}
		}

		// ════════════════════════════════════════════════════════════
		// Scene Selection Sync
		// ════════════════════════════════════════════════════════════

		private void OnSceneSelectionChanged()
		{
			if (!_sceneSync || !EditorApplication.isPlaying || _currentWorld == null) return;

			var go = Selection.activeGameObject;
			if (go == null) return;

			// Scene → Inspector: find ECS entity for selected GameObject
			foreach (var entry in _entities)
			{
				var viewGo = GetViewGameObject(entry.Id);
				if (viewGo == go)
				{
					_selectedEntityId = entry.Id;
					_keyboardFocusIndex = _filteredIndices.IndexOf(_entities.IndexOf(entry));
					RefreshSelectedComponents();
					Repaint();
					return;
				}
			}
		}

		private void SyncToScene(int entityId)
		{
			if (!EditorApplication.isPlaying) return;

			var viewGo = GetViewGameObject(entityId);
			if (viewGo != null)
			{
				// Temporarily disable sync to avoid feedback loop
				_sceneSync = false;
				Selection.activeGameObject = viewGo;
				_sceneSync = true;

				if (SceneView.lastActiveSceneView != null)
					SceneView.lastActiveSceneView.Frame(new Bounds(viewGo.transform.position, Vector3.one * 5f), false);
			}
		}

		// ── Reflection-based EntityViewSystem access ───────────────

		private static object s_cachedEvs;
		private static MethodInfo s_tryGetViewMethod;
		private static double s_evsLookupTime;

		/// <summary>
		/// Get the GameObject for an entity via EntityViewSystem.TryGetView (reflection).
		/// </summary>
		private static GameObject GetViewGameObject(int entityId)
		{
			EnsureEvsCache();
			if (s_cachedEvs == null || s_tryGetViewMethod == null) return null;

			try
			{
				var args = new object[] { entityId, null };
				var result = (bool)s_tryGetViewMethod.Invoke(s_cachedEvs, args);
				if (result && args[1] != null)
				{
					// args[1] is ServerControllable (MonoBehaviour)
					var mono = args[1] as MonoBehaviour;
					return mono != null ? mono.gameObject : null;
				}
			}
			catch { }
			return null;
		}

		private static void EnsureEvsCache()
		{
			if (s_cachedEvs != null && EditorApplication.timeSinceStartup - s_evsLookupTime < 1.0)
				return;

			s_evsLookupTime = EditorApplication.timeSinceStartup;
			s_cachedEvs = null;
			s_tryGetViewMethod = null;

			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				var hfType = asm.GetType("Core.HF");
				if (hfType == null) continue;

				Type evsType = null;
				foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
				{
					evsType = a.GetType("ORO.EntityViewSystem");
					if (evsType != null) break;
				}
				if (evsType == null) break;

				var getMethod = hfType.GetMethod("Get", Type.EmptyTypes);
				if (getMethod == null) break;

				try
				{
					s_cachedEvs = getMethod.MakeGenericMethod(evsType).Invoke(null, null);
					if (s_cachedEvs != null)
						s_tryGetViewMethod = evsType.GetMethod("TryGetView");
				}
				catch { }
				break;
			}
		}

		// ════════════════════════════════════════════════════════════
		// Main GUI
		// ════════════════════════════════════════════════════════════

		private void OnGUI()
		{
			InitStyles();

			if (!EditorApplication.isPlaying)
			{
				DrawNotPlayingMessage();
				return;
			}

			RefreshWorlds();

			if (_worldNames.Length == 0)
			{
				EditorGUILayout.HelpBox("No worlds found.\n\nRegister via MassiveWorldRegistry.Register(\"name\", world)\nor mark a struct with [StaticWorldType].", MessageType.Info);
				return;
			}

			DrawToolbar();
			HandleKeyboard();
			DrawSplitView();
		}

		// ════════════════════════════════════════════════════════════
		// Keyboard Navigation
		// ════════════════════════════════════════════════════════════

		private void HandleKeyboard()
		{
			if (Event.current.type != EventType.KeyDown) return;
			if (_filteredIndices.Count == 0) return;

			var key = Event.current.keyCode;
			bool handled = false;

			if (key == KeyCode.DownArrow)
			{
				_keyboardFocusIndex = Mathf.Min(_keyboardFocusIndex + 1, _filteredIndices.Count - 1);
				handled = true;
			}
			else if (key == KeyCode.UpArrow)
			{
				_keyboardFocusIndex = Mathf.Max(_keyboardFocusIndex - 1, 0);
				handled = true;
			}
			else if (key == KeyCode.Return || key == KeyCode.KeypadEnter)
			{
				// Sync to scene on Enter
				if (_selectedEntityId >= 0)
					SyncToScene(_selectedEntityId);
				handled = true;
			}

			if (handled)
			{
				if (_keyboardFocusIndex >= 0 && _keyboardFocusIndex < _filteredIndices.Count)
				{
					var entry = _entities[_filteredIndices[_keyboardFocusIndex]];
					_selectedEntityId = entry.Id;
					RefreshSelectedComponents();

					// Auto-scroll to selected row
					float rowY = _keyboardFocusIndex * RowHeight;
					if (rowY < _entityListScroll.y || rowY > _entityListScroll.y + position.height - 100)
						_entityListScroll.y = rowY - 60;
				}
				Event.current.Use();
				Repaint();
			}
		}

		// ════════════════════════════════════════════════════════════
		// Toolbar
		// ════════════════════════════════════════════════════════════

		private void DrawToolbar()
		{
			using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
			{
				// World selector
				EditorGUILayout.LabelField("World", GUILayout.Width(40));
				var newWorld = EditorGUILayout.Popup(_selectedWorldIndex, _worldNames, EditorStyles.toolbarPopup, GUILayout.Width(150));
				if (newWorld != _selectedWorldIndex)
				{
					_selectedWorldIndex = newWorld;
					_selectedEntityId = -1;
					_keyboardFocusIndex = -1;
					_filterComponentIds.Clear();
					_filterEnabled = false;
					_allComponentTypes.Clear();
					_componentColors.Clear();
					if (_selectedWorldIndex < _worldNames.Length)
						EditorPrefs.SetString(PrefWorldName, _worldNames[_selectedWorldIndex]);
				}

				GUILayout.Space(4);

				// Live toggle
				_liveUpdate = GUILayout.Toggle(_liveUpdate, new GUIContent(" Live", EditorGUIUtility.IconContent("d_PlayButton On").image), EditorStyles.toolbarButton, GUILayout.Width(56));

				// Scene sync toggle
				_sceneSync = GUILayout.Toggle(_sceneSync, new GUIContent(" Sync", EditorGUIUtility.IconContent("d_SceneViewTools").image), EditorStyles.toolbarButton, GUILayout.Width(56));

				GUILayout.Space(4);

				// Search
				_searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.MinWidth(80));

				GUILayout.FlexibleSpace();

				// Component filter
				var filterLabel = _filterEnabled ? $"Filter ({_filterComponentIds.Count})" : "Filter";
				if (GUILayout.Button(filterLabel, EditorStyles.toolbarDropDown, GUILayout.Width(75)))
					ShowComponentFilterMenu();

				// Stats
				if (_currentWorld != null)
					EditorGUILayout.LabelField($"E:{_currentWorld.Entities.Count}  C:{_currentWorld.Sets.ComponentCount}", s_statsStyle, GUILayout.Width(80));
			}
		}

		// ════════════════════════════════════════════════════════════
		// Split View
		// ════════════════════════════════════════════════════════════

		private void DrawSplitView()
		{
			var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
			if (rect.height < 10)
				rect = new Rect(0, ToolbarHeight + 4, position.width, position.height - ToolbarHeight - 4);

			_splitPosition = Mathf.Clamp(_splitPosition, MinPaneWidth, Mathf.Max(rect.width - MinPaneWidth, MinPaneWidth + 1));

			// Splitter
			var splitterRect = new Rect(rect.x + _splitPosition, rect.y, SplitterWidth, rect.height);
			EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);

			if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
			{
				_isDraggingSplitter = true;
				Event.current.Use();
			}
			if (_isDraggingSplitter)
			{
				if (Event.current.type == EventType.MouseDrag)
				{
					_splitPosition = Event.current.mousePosition.x - rect.x;
					Event.current.Use();
					Repaint();
				}
				if (Event.current.type == EventType.MouseUp)
				{
					_isDraggingSplitter = false;
					EditorPrefs.SetFloat(PrefSplitPos, _splitPosition);
					Event.current.Use();
				}
			}
			EditorGUI.DrawRect(splitterRect, SplitterColor);

			// Refresh data
			RefreshEntities();
			BuildFilteredIndices();

			// Left pane
			var leftRect = new Rect(rect.x, rect.y, _splitPosition, rect.height);
			GUILayout.BeginArea(leftRect);
			DrawEntityList();
			GUILayout.EndArea();

			// Right pane
			var rightRect = new Rect(rect.x + _splitPosition + SplitterWidth, rect.y, rect.width - _splitPosition - SplitterWidth, rect.height);
			GUILayout.BeginArea(rightRect);
			DrawComponentInspector();
			GUILayout.EndArea();
		}

		// ════════════════════════════════════════════════════════════
		// Entity List (Left Pane)
		// ════════════════════════════════════════════════════════════

		private void BuildFilteredIndices()
		{
			_filteredIndices.Clear();
			for (int i = 0; i < _entities.Count; i++)
			{
				var entry = _entities[i];

				// Search filter
				if (!string.IsNullOrEmpty(_searchFilter))
				{
					bool match = entry.Id.ToString().Contains(_searchFilter);
					if (!match && entry.TypeLabel != null)
						match = entry.TypeLabel.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0;
					if (!match && entry.Components != null)
					{
						foreach (var comp in entry.Components)
						{
							if (comp.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
							{
								match = true;
								break;
							}
						}
					}
					if (!match) continue;
				}

				// Component filter
				if (_filterEnabled && _filterComponentIds.Count > 0 && entry.Components != null)
				{
					bool hasAll = true;
					foreach (var filterId in _filterComponentIds)
					{
						bool found = false;
						foreach (var comp in entry.Components)
						{
							if (comp.ComponentId == filterId) { found = true; break; }
						}
						if (!found) { hasAll = false; break; }
					}
					if (!hasAll) continue;
				}

				_filteredIndices.Add(i);
			}
		}

		private void DrawEntityList()
		{
			// Column headers
			using (new EditorGUILayout.HorizontalScope(s_columnHeaderStyle, GUILayout.Height(18)))
			{
				EditorGUILayout.LabelField("ID", EditorStyles.miniBoldLabel, GUILayout.Width(35));
				EditorGUILayout.LabelField("Type", EditorStyles.miniBoldLabel, GUILayout.Width(60));
				EditorGUILayout.LabelField("Components", EditorStyles.miniBoldLabel);
			}

			_entityListScroll = EditorGUILayout.BeginScrollView(_entityListScroll);

			for (int fi = 0; fi < _filteredIndices.Count; fi++)
			{
				DrawEntityRow(_entities[_filteredIndices[fi]], fi);
			}

			if (_filteredIndices.Count == 0 && _entities.Count > 0)
			{
				EditorGUILayout.LabelField("No entities match the current filter.", EditorStyles.centeredGreyMiniLabel);
			}
			else if (_entities.Count == 0)
			{
				EditorGUILayout.LabelField("No entities in world.", EditorStyles.centeredGreyMiniLabel);
			}

			EditorGUILayout.EndScrollView();
		}

		private void DrawEntityRow(EntityEntry entry, int filteredIndex)
		{
			bool isSelected = entry.Id == _selectedEntityId;
			var rowRect = EditorGUILayout.BeginHorizontal(s_entityRowStyle, GUILayout.Height(RowHeight));

			// Alternating row background
			if (Event.current.type == EventType.Repaint)
			{
				if (isSelected)
					EditorGUI.DrawRect(rowRect, RowSelectedColor);
				else if (rowRect.Contains(Event.current.mousePosition))
					EditorGUI.DrawRect(rowRect, RowHoverColor);
				else if (filteredIndex % 2 == 0)
					EditorGUI.DrawRect(rowRect, RowEvenColor);
			}

			// Click to select
			if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
			{
				_selectedEntityId = entry.Id;
				_keyboardFocusIndex = filteredIndex;
				RefreshSelectedComponents();

				// Double-click: sync to scene
				if (Event.current.clickCount == 2)
					SyncToScene(entry.Id);

				Event.current.Use();
				Repaint();
			}

			// Right-click context menu
			if (Event.current.type == EventType.ContextClick && rowRect.Contains(Event.current.mousePosition))
			{
				ShowEntityContextMenu(entry);
				Event.current.Use();
			}

			// Entity ID
			EditorGUILayout.LabelField($"#{entry.Id}", GUILayout.Width(35));

			// Type label
			if (entry.TypeLabel != null)
				EditorGUILayout.LabelField(entry.TypeLabel, s_statsStyle, GUILayout.Width(60));
			else
				GUILayout.Space(64);

			// Component thumbnails
			if (entry.Components != null)
			{
				foreach (var comp in entry.Components)
					DrawComponentThumbnail(comp);
			}

			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();
		}

		private void DrawComponentThumbnail(ComponentTypeInfo comp)
		{
			var rect = GUILayoutUtility.GetRect(ThumbnailSize, ThumbnailSize, GUILayout.Width(ThumbnailSize));
			rect.y += 3;

			// Rounded-ish background
			EditorGUI.DrawRect(rect, comp.Color);

			if (s_thumbnailStyle == null)
			{
				s_thumbnailStyle = new GUIStyle(EditorStyles.miniLabel)
				{
					alignment = TextAnchor.MiddleCenter,
					fontSize = 8,
					fontStyle = FontStyle.Bold,
					normal = { textColor = Color.white },
					padding = new RectOffset(0, 0, 0, 0)
				};
			}
			GUI.Label(rect, comp.ShortName, s_thumbnailStyle);

			if (rect.Contains(Event.current.mousePosition))
				GUI.tooltip = comp.Name;

			GUILayout.Space(ThumbnailSpacing);
		}

		// ════════════════════════════════════════════════════════════
		// Context Menu
		// ════════════════════════════════════════════════════════════

		private void ShowEntityContextMenu(EntityEntry entry)
		{
			var menu = new GenericMenu();
			menu.AddItem(new GUIContent("Select in Scene"), false, () => SyncToScene(entry.Id));
			menu.AddItem(new GUIContent("Copy Entity as JSON"), false, () => CopyEntityAsJson(entry));
			menu.AddSeparator("");
			menu.AddItem(new GUIContent($"Entity #{entry.Id}  v{entry.Version}"), false, () => { });
			if (entry.Components != null)
			{
				foreach (var comp in entry.Components)
					menu.AddDisabledItem(new GUIContent($"  {comp.Name}"));
			}
			menu.ShowAsContext();
		}

		private void CopyEntityAsJson(EntityEntry entry)
		{
			if (_currentWorld == null) return;
			var sb = new StringBuilder();
			sb.AppendLine("{");
			sb.AppendLine($"  \"id\": {entry.Id},");
			sb.AppendLine($"  \"version\": {entry.Version},");
			if (entry.TypeLabel != null)
				sb.AppendLine($"  \"type\": \"{entry.TypeLabel}\",");
			sb.AppendLine("  \"components\": {");

			var sets = _currentWorld.Sets;
			bool first = true;
			for (int i = 0; i < sets.ComponentCount; i++)
			{
				var bitSet = sets.LookupByComponentId[i];
				if (bitSet == null || !bitSet.Has(entry.Id)) continue;
				var type = sets.TypeOf(bitSet);
				if (type == null) continue;

				if (!first) sb.AppendLine(",");
				first = false;
				sb.Append($"    \"{type.Name}\": ");

				if (bitSet is IDataSet dataSet)
				{
					try
					{
						var data = dataSet.GetRaw(entry.Id);
						sb.Append(JsonUtility.ToJson(data, true).Replace("\n", "\n    "));
					}
					catch
					{
						sb.Append("\"<error reading>\"");
					}
				}
				else
				{
					sb.Append("\"(tag)\"");
				}
			}

			sb.AppendLine();
			sb.AppendLine("  }");
			sb.AppendLine("}");

			EditorGUIUtility.systemCopyBuffer = sb.ToString();
			Debug.Log($"[MassiveInspector] Copied Entity #{entry.Id} to clipboard.");
		}

		// ════════════════════════════════════════════════════════════
		// Component Inspector (Right Pane)
		// ════════════════════════════════════════════════════════════

		private void DrawComponentInspector()
		{
			if (_selectedEntityId < 0 || _currentWorld == null)
			{
				EditorGUILayout.LabelField("Select an entity to inspect.", EditorStyles.centeredGreyMiniLabel);
				return;
			}

			if (!_currentWorld.Entities.IsAlive(_selectedEntityId))
			{
				EditorGUILayout.HelpBox($"Entity #{_selectedEntityId} is no longer alive.", MessageType.Warning);
				_selectedEntityId = -1;
				return;
			}

			// Header
			var entity = _currentWorld.Entities.GetEntity(_selectedEntityId);
			using (new EditorGUILayout.HorizontalScope())
			{
				EditorGUILayout.LabelField($"Entity #{entity.Id}  (v{entity.Version})", s_headerStyle);
				GUILayout.FlexibleSpace();
				if (GUILayout.Button("Select in Scene", EditorStyles.miniButton, GUILayout.Width(100)))
					SyncToScene(entity.Id);
			}
			EditorGUILayout.Space(2);

			// Refresh if live
			if (_liveUpdate)
				RefreshSelectedComponents();

			_inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll);

			foreach (var comp in _selectedComponents)
				DrawComponentSection(comp);

			EditorGUILayout.EndScrollView();
		}

		private void DrawComponentSection(ComponentEntry comp)
		{
			var foldoutKey = comp.Name;
			if (!_expandedComponents.Contains(foldoutKey))
				_expandedComponents.Add(foldoutKey);

			bool wasExpanded = _expandedComponents.Contains(foldoutKey);

			var headerRect = EditorGUILayout.BeginHorizontal(s_componentHeaderStyle);
			var color = GetComponentColor(comp.Type);

			// Color bar
			if (Event.current.type == EventType.Repaint)
				EditorGUI.DrawRect(new Rect(headerRect.x, headerRect.y, 3, headerRect.height), color);

			GUILayout.Space(8);
			bool expanded = EditorGUILayout.Foldout(wasExpanded, comp.Name, true);
			if (expanded != wasExpanded)
			{
				if (expanded) _expandedComponents.Add(foldoutKey);
				else _expandedComponents.Remove(foldoutKey);
			}

			GUILayout.FlexibleSpace();

			// Badge
			var thumbRect = GUILayoutUtility.GetRect(ThumbnailSize, ThumbnailSize);
			thumbRect.y += 2;
			EditorGUI.DrawRect(thumbRect, color);
			GUI.Label(thumbRect, GetShortName(comp.Type), s_thumbnailStyle);

			EditorGUILayout.EndHorizontal();

			if (expanded && comp.Data != null)
			{
				EditorGUI.indentLevel++;
				DrawComponentFields(comp.Data, comp.Fields, foldoutKey);
				EditorGUI.indentLevel--;
			}
			else if (expanded && comp.Data == null && (comp.Fields == null || comp.Fields.Length == 0))
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.LabelField("(tag component)", EditorStyles.miniLabel);
				EditorGUI.indentLevel--;
			}

			EditorGUILayout.Space(1);
		}

		private void DrawComponentFields(object data, FieldInfo[] fields, string parentKey)
		{
			if (fields == null || fields.Length == 0)
			{
				EditorGUILayout.LabelField("(tag component)", EditorStyles.miniLabel);
				return;
			}

			foreach (var field in fields)
			{
				// Skip backing fields
				if (field.Name.StartsWith("<")) continue;

				var value = field.GetValue(data);
				var fieldKey = $"{parentKey}.{field.Name}";

				if (value == null)
				{
					using (new EditorGUILayout.HorizontalScope())
					{
						EditorGUILayout.LabelField(field.Name, s_fieldLabelStyle, GUILayout.Width(130));
						EditorGUILayout.LabelField("null", s_fieldValueStyle);
					}
					continue;
				}

				var valueType = value.GetType();

				if (IsSimpleType(valueType))
				{
					using (new EditorGUILayout.HorizontalScope())
					{
						EditorGUILayout.LabelField(field.Name, s_fieldLabelStyle, GUILayout.Width(130));
						DrawValueField(value, valueType);
					}
				}
				else if (IsListModelType(valueType))
				{
					DrawListModelField(field.Name, value, valueType, fieldKey);
				}
				else if (valueType.IsValueType || valueType.IsClass)
				{
					bool wasExpanded = _expandedFields.Contains(fieldKey);
					bool expanded = EditorGUILayout.Foldout(wasExpanded, $"{field.Name}  ({valueType.Name})", true);
					if (expanded != wasExpanded)
					{
						if (expanded) _expandedFields.Add(fieldKey);
						else _expandedFields.Remove(fieldKey);
					}
					if (expanded)
					{
						EditorGUI.indentLevel++;
						DrawComponentFields(value, GetFieldsCached(valueType), fieldKey);
						EditorGUI.indentLevel--;
					}
				}
			}
		}

		private void DrawValueField(object value, Type type)
		{
			if (type == typeof(Vector3))
			{
				var v = (Vector3)value;
				EditorGUILayout.LabelField($"({v.x:F2}, {v.y:F2}, {v.z:F2})", s_fieldValueStyle);
			}
			else if (type == typeof(Vector2))
			{
				var v = (Vector2)value;
				EditorGUILayout.LabelField($"({v.x:F2}, {v.y:F2})", s_fieldValueStyle);
			}
			else if (type == typeof(Vector2Int))
			{
				var v = (Vector2Int)value;
				EditorGUILayout.LabelField($"({v.x}, {v.y})", s_fieldValueStyle);
			}
			else if (type == typeof(Vector3Int))
			{
				var v = (Vector3Int)value;
				EditorGUILayout.LabelField($"({v.x}, {v.y}, {v.z})", s_fieldValueStyle);
			}
			else if (type == typeof(Quaternion))
			{
				var q = (Quaternion)value;
				EditorGUILayout.LabelField($"({q.x:F2}, {q.y:F2}, {q.z:F2}, {q.w:F2})", s_fieldValueStyle);
			}
			else if (type == typeof(Color))
			{
				var c = (Color)value;
				var r = GUILayoutUtility.GetRect(30, 14, GUILayout.Width(30));
				EditorGUI.DrawRect(r, c);
				EditorGUILayout.LabelField($"({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})", s_fieldValueStyle);
			}
			else if (type == typeof(bool))
			{
				EditorGUILayout.LabelField((bool)value ? "true" : "false", s_fieldValueStyle);
			}
			else if (type == typeof(float))
			{
				EditorGUILayout.LabelField(((float)value).ToString("F3"), s_fieldValueStyle);
			}
			else if (type == typeof(double))
			{
				EditorGUILayout.LabelField(((double)value).ToString("F3"), s_fieldValueStyle);
			}
			else if (type == typeof(Entifier))
			{
				var ent = (Entifier)value;
				if (GUILayout.Button($"-> Entity({ent.Id}, v{ent.Version})", EditorStyles.linkLabel))
				{
					_selectedEntityId = ent.Id;
					RefreshSelectedComponents();
				}
			}
			else if (type.IsEnum)
			{
				EditorGUILayout.LabelField(value.ToString(), s_fieldValueStyle);
			}
			else
			{
				EditorGUILayout.LabelField(value.ToString(), s_fieldValueStyle);
			}
		}

		// ════════════════════════════════════════════════════════════
		// ListModel Display
		// ════════════════════════════════════════════════════════════

		private static bool IsListModelType(Type type)
		{
			return type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("ListModel");
		}

		private void DrawListModelField(string fieldName, object value, Type valueType, string fieldKey)
		{
			// ListModel<T> has Count and Items fields
			var countField = valueType.GetField("Count");
			var itemsField = valueType.GetField("Items");

			int count = countField != null ? (int)countField.GetValue(value) : -1;
			string label = count >= 0 ? $"{fieldName}  (ListModel, {count} items)" : $"{fieldName}  (ListModel)";

			bool wasExpanded = _expandedFields.Contains(fieldKey);
			bool expanded = EditorGUILayout.Foldout(wasExpanded, label, true);
			if (expanded != wasExpanded)
			{
				if (expanded) _expandedFields.Add(fieldKey);
				else _expandedFields.Remove(fieldKey);
			}

			if (expanded && _currentWorld != null && count > 0)
			{
				EditorGUI.indentLevel++;

				// Try to read items via allocator
				var allocator = _currentWorld.Allocator;
				var elementType = valueType.GetGenericArguments()[0];

				// Use the indexer: listModel[allocator, index]
				var indexerMethod = valueType.GetMethod("get_Item", new[] { typeof(Allocator), typeof(int) });
				if (indexerMethod != null)
				{
					int displayCount = Mathf.Min(count, 50); // cap display
					for (int i = 0; i < displayCount; i++)
					{
						try
						{
							var item = indexerMethod.Invoke(value, new object[] { (Allocator)_currentWorld, i });
							using (new EditorGUILayout.HorizontalScope())
							{
								EditorGUILayout.LabelField($"[{i}]", s_fieldLabelStyle, GUILayout.Width(40));
								if (item != null && IsSimpleType(item.GetType()))
									DrawValueField(item, item.GetType());
								else
									EditorGUILayout.LabelField(item?.ToString() ?? "null", s_fieldValueStyle);
							}
						}
						catch
						{
							EditorGUILayout.LabelField($"[{i}]  <error>", s_fieldValueStyle);
						}
					}
					if (count > displayCount)
						EditorGUILayout.LabelField($"... and {count - displayCount} more", EditorStyles.miniLabel);
				}
				else
				{
					EditorGUILayout.LabelField("(cannot read: no indexer found)", EditorStyles.miniLabel);
				}

				EditorGUI.indentLevel--;
			}
			else if (expanded && count == 0)
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.LabelField("(empty)", EditorStyles.miniLabel);
				EditorGUI.indentLevel--;
			}
		}

		// ════════════════════════════════════════════════════════════
		// Component Filter
		// ════════════════════════════════════════════════════════════

		private void ShowComponentFilterMenu()
		{
			if (_currentWorld == null) return;
			RefreshComponentTypes();

			var menu = new GenericMenu();
			menu.AddItem(new GUIContent("(Clear Filter)"), false, () =>
			{
				_filterComponentIds.Clear();
				_filterEnabled = false;
			});
			menu.AddSeparator("");

			foreach (var comp in _allComponentTypes)
			{
				bool isOn = _filterComponentIds.Contains(comp.ComponentId);
				var c = comp;
				menu.AddItem(new GUIContent(comp.Name), isOn, () =>
				{
					if (_filterComponentIds.Contains(c.ComponentId))
						_filterComponentIds.Remove(c.ComponentId);
					else
						_filterComponentIds.Add(c.ComponentId);
					_filterEnabled = _filterComponentIds.Count > 0;
				});
			}

			menu.ShowAsContext();
		}

		// ════════════════════════════════════════════════════════════
		// Not Playing
		// ════════════════════════════════════════════════════════════

		private void DrawNotPlayingMessage()
		{
			GUILayout.FlexibleSpace();
			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.FlexibleSpace();
				using (new EditorGUILayout.VerticalScope(GUILayout.Width(320)))
				{
					GUILayout.Space(20);
					EditorGUILayout.LabelField("Massive State Inspector", s_headerStyle);
					GUILayout.Space(8);
					EditorGUILayout.HelpBox(
						"Enter Play Mode to inspect ECS state.\n\n" +
						"Features:\n" +
						"  - Live entity & component browsing\n" +
						"  - Search by ID, type, or component name\n" +
						"  - Component type filtering\n" +
						"  - Bidirectional scene selection sync\n" +
						"  - Keyboard navigation (arrow keys)\n" +
						"  - Right-click to copy entity as JSON",
						MessageType.Info);
					GUILayout.Space(8);
					if (GUILayout.Button("Enter Play Mode", GUILayout.Height(28)))
						EditorApplication.isPlaying = true;
				}
				GUILayout.FlexibleSpace();
			}
			GUILayout.FlexibleSpace();
		}

		// ════════════════════════════════════════════════════════════
		// Data Refresh
		// ════════════════════════════════════════════════════════════

		private void RefreshWorlds()
		{
			try
			{
				var allNames = new List<string>();
				var allWorlds = new List<World>();

				for (int i = 0; i < MassiveWorldRegistry.Count; i++)
				{
					allNames.Add(MassiveWorldRegistry.GetName(i));
					allWorlds.Add(MassiveWorldRegistry.GetWorld(i));
				}

				StaticWorlds.WarmupAll();
				var staticNames = StaticWorlds.WorldNames;
				var staticWorlds = StaticWorlds.Worlds;
				for (int i = 0; i < staticNames.Length; i++)
				{
					allNames.Add(staticNames[i]);
					allWorlds.Add(staticWorlds[i]);
				}

				_worldNames = allNames.ToArray();
				_worldLookup = allWorlds;

				// Restore saved world selection
				if (_worldNames.Length > 0)
				{
					var savedName = EditorPrefs.GetString(PrefWorldName, "");
					if (!string.IsNullOrEmpty(savedName))
					{
						for (int i = 0; i < _worldNames.Length; i++)
						{
							if (_worldNames[i] == savedName)
							{
								_selectedWorldIndex = i;
								break;
							}
						}
					}
					_selectedWorldIndex = Mathf.Clamp(_selectedWorldIndex, 0, _worldNames.Length - 1);
					_currentWorld = _worldLookup[_selectedWorldIndex];
				}
				else
				{
					_currentWorld = null;
				}
			}
			catch
			{
				_worldNames = Array.Empty<string>();
				_currentWorld = null;
			}
		}

		private void RefreshEntities()
		{
			_entities.Clear();
			if (_currentWorld == null) return;

			try
			{
				RefreshComponentTypes();

				using (var enumerator = _currentWorld.Entities.GetEnumerator())
				{
					while (enumerator.MoveNext())
					{
						var entity = enumerator.Current;
						var entry = new EntityEntry
						{
							Id = entity.Id,
							Version = entity.Version,
							TypeLabel = GetEntityTypeLabel(entity.Id),
							Components = GetEntityComponentTypes(entity.Id)
						};
						_entities.Add(entry);
					}
				}

				_entities.Sort((a, b) => a.Id.CompareTo(b.Id));
			}
			catch
			{
				// Fallback: manual scan
				try
				{
					var usedIds = _currentWorld.Entities.UsedIds;
					for (int id = 0; id < usedIds; id++)
					{
						if (_currentWorld.Entities.IsAlive(id))
						{
							_entities.Add(new EntityEntry
							{
								Id = id,
								Version = _currentWorld.Entities.Versions[id],
								TypeLabel = GetEntityTypeLabel(id),
								Components = GetEntityComponentTypes(id)
							});
						}
					}
				}
				catch { }
			}
		}

		/// <summary>
		/// Try to read EntityTypeTag.Type enum value as a string label.
		/// </summary>
		private string GetEntityTypeLabel(int entityId)
		{
			var sets = _currentWorld.Sets;
			for (int i = 0; i < sets.ComponentCount; i++)
			{
				var bitSet = sets.LookupByComponentId[i];
				if (bitSet == null) continue;
				var type = sets.TypeOf(bitSet);
				if (type == null || type.Name != "EntityTypeTag") continue;
				if (!bitSet.Has(entityId)) return null;

				if (bitSet is IDataSet dataSet)
				{
					try
					{
						var data = dataSet.GetRaw(entityId);
						var typeField = type.GetField("Type") ?? type.GetFields()[0];
						return typeField.GetValue(data)?.ToString();
					}
					catch { }
				}
				return null;
			}
			return null;
		}

		private void RefreshComponentTypes()
		{
			if (_currentWorld == null) return;
			if (_allComponentTypes.Count == _currentWorld.Sets.ComponentCount) return;

			_allComponentTypes.Clear();
			var sets = _currentWorld.Sets;

			for (int i = 0; i < sets.ComponentCount; i++)
			{
				var bitSet = sets.LookupByComponentId[i];
				if (bitSet == null) continue;
				var type = sets.TypeOf(bitSet);
				if (type == null) continue;

				_allComponentTypes.Add(new ComponentTypeInfo
				{
					Type = type,
					Name = type.Name,
					ShortName = GetShortName(type),
					ComponentId = i,
					Color = GetComponentColor(type)
				});
			}
		}

		private List<ComponentTypeInfo> GetEntityComponentTypes(int entityId)
		{
			var result = new List<ComponentTypeInfo>();
			if (_currentWorld == null) return result;

			var sets = _currentWorld.Sets;
			for (int i = 0; i < sets.ComponentCount; i++)
			{
				var bitSet = sets.LookupByComponentId[i];
				if (bitSet == null || !bitSet.Has(entityId)) continue;
				var type = sets.TypeOf(bitSet);
				if (type == null) continue;

				result.Add(new ComponentTypeInfo
				{
					Type = type,
					Name = type.Name,
					ShortName = GetShortName(type),
					ComponentId = i,
					Color = GetComponentColor(type)
				});
			}
			return result;
		}

		private void RefreshSelectedComponents()
		{
			_selectedComponents.Clear();
			if (_currentWorld == null || _selectedEntityId < 0) return;
			if (!_currentWorld.Entities.IsAlive(_selectedEntityId)) return;

			var sets = _currentWorld.Sets;
			for (int i = 0; i < sets.ComponentCount; i++)
			{
				var bitSet = sets.LookupByComponentId[i];
				if (bitSet == null || !bitSet.Has(_selectedEntityId)) continue;
				var type = sets.TypeOf(bitSet);
				if (type == null) continue;

				object data = null;
				FieldInfo[] fields = null;

				if (bitSet is IDataSet dataSet)
				{
					try
					{
						data = dataSet.GetRaw(_selectedEntityId);
						fields = GetFieldsCached(type);
					}
					catch { }
				}

				_selectedComponents.Add(new ComponentEntry
				{
					Type = type,
					Name = type.Name,
					ComponentId = i,
					Data = data,
					Fields = fields
				});
			}
		}

		// ════════════════════════════════════════════════════════════
		// Helpers
		// ════════════════════════════════════════════════════════════

		private static readonly Dictionary<Type, FieldInfo[]> s_fieldCache = new Dictionary<Type, FieldInfo[]>();

		private static FieldInfo[] GetFieldsCached(Type type)
		{
			if (!s_fieldCache.TryGetValue(type, out var fields))
			{
				fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				s_fieldCache[type] = fields;
			}
			return fields;
		}

		private static bool IsSimpleType(Type type)
		{
			return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
				|| type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4)
				|| type == typeof(Vector2Int) || type == typeof(Vector3Int)
				|| type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Color32)
				|| type == typeof(Entifier);
		}

		private static string GetShortName(Type type)
		{
			var name = type.Name;
			if (name.Length <= 2) return name;
			var sb = new StringBuilder(2);
			sb.Append(name[0]);
			for (int i = 1; i < name.Length && sb.Length < 2; i++)
			{
				if (char.IsUpper(name[i])) sb.Append(name[i]);
			}
			if (sb.Length < 2 && name.Length > 1) sb.Append(name[1]);
			return sb.ToString();
		}

		private Color GetComponentColor(Type type)
		{
			if (!_componentColors.TryGetValue(type, out var color))
			{
				var hash = type.FullName?.GetHashCode() ?? type.Name.GetHashCode();
				var hue = (float)(((uint)hash) % 360) / 360f;
				color = Color.HSVToRGB(hue, 0.55f, 0.75f);
				_componentColors[type] = color;
			}
			return color;
		}

		// ════════════════════════════════════════════════════════════
		// Styles
		// ════════════════════════════════════════════════════════════

		private static void InitStyles()
		{
			if (s_entityRowStyle != null) return;

			s_entityRowStyle = new GUIStyle()
			{
				padding = new RectOffset(4, 4, 2, 2),
				margin = new RectOffset(0, 0, 0, 0),
				fixedHeight = RowHeight
			};

			s_headerStyle = new GUIStyle(EditorStyles.boldLabel)
			{
				fontSize = 12,
				padding = new RectOffset(4, 4, 2, 2)
			};

			s_componentHeaderStyle = new GUIStyle("RL Header")
			{
				padding = new RectOffset(8, 4, 2, 2),
				margin = new RectOffset(0, 0, 1, 0),
				fixedHeight = 22
			};

			s_fieldLabelStyle = new GUIStyle(EditorStyles.label)
			{
				normal = { textColor = new Color(0.65f, 0.65f, 0.65f, 1f) }
			};

			s_fieldValueStyle = new GUIStyle(EditorStyles.label)
			{
				richText = true
			};

			s_statsStyle = new GUIStyle(EditorStyles.miniLabel)
			{
				alignment = TextAnchor.MiddleRight,
				normal = { textColor = new Color(0.55f, 0.55f, 0.55f, 1f) }
			};

			s_columnHeaderStyle = new GUIStyle()
			{
				padding = new RectOffset(4, 4, 2, 2),
				margin = new RectOffset(0, 0, 0, 0),
				fixedHeight = 18
			};
		}
	}
}
