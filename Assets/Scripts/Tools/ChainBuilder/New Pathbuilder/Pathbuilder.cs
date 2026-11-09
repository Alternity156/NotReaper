using NotReaper.Grid;
using NotReaper.Models;
using NotReaper.Notifications;
using NotReaper.Targets;
using NotReaper.Timing;
using NotReaper.Tools.ChainBuilder;
using NotReaper.UserInput;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NotReaper.Tools.PathBuilder
{
	[RequireComponent(typeof(SegmentPool))]
	public class Pathbuilder : NRInput<PathbuilderKeybinds>
	{
        #region Inspector References
        [Space, Header("References")]
		[SerializeField] private Transform canvas;
		[SerializeField] private Transform pathbuilderParent;
		[SerializeField] private PathbuilderNode nodePrefab;
		[Space, Header("Active Segment Indicator")]
		[SerializeField] private LineRenderer activeSegmentIndicator;
        #endregion

        #region Fields
        #region Dependencies
        [NRInject] private Timeline timeline;
		[NRInject] private PathbuilderUI ui;
        #endregion

        #region Classes
        private SegmentPool segmentPool;
		private PathbuilderCalculator calculator;
        #endregion

        #region Data
        private Target activeTarget;
		private Segment activeSegment;
		private Segment tempSegment;
		private Point activePoint;
		private List<Segment> segments = new List<Segment>();
		private bool alternateHands;
		private bool isSegmentScope = true;
		private PathbuilderData.Interval intervalOverride = new PathbuilderData.Interval(1, 16);
		private QNT_Duration beatLengthOverride = new QNT_Duration(480);
        #endregion

        #region Reposition Mode
		/// <summary>
		/// True when the pathbuilder was opened with several existing targets selected.
		/// Instead of generating new nodes, the path moves the selected targets along it,
		/// keeping their timing. activeTarget is the earliest selected target (the path start).
		/// </summary>
		private bool repositionMode = false;
		private List<TargetData> repositionTargets = new List<TargetData>();
		private List<Vector2> repositionStartPositions = new List<Vector2>();
        #endregion

        #region State
        private bool snapToGrid = false;
		private bool dragNote = false;
		private bool isMouseDown = false;
		private Vector2 dragStartPos;
		public bool isActive;
        #endregion

        #region Utility
        private TargetIcon[] _iconsUnderMouse = new TargetIcon[0];
		/** Fetch all icons currently under the mouse
     *  Will only ever happen once per frame */
		public TargetIcon[] iconsUnderMouse
		{
			get
			{
				return _iconsUnderMouse = _iconsUnderMouse == null
					? MouseUtil.IconsUnderMouse(timeline)
					: _iconsUnderMouse;
			}
			set { _iconsUnderMouse = value; }
		}

		// Fetch the highest priority target (closest to current time)
		public TargetIcon iconUnderMouse
		{
			get
			{
				return iconsUnderMouse != null && iconsUnderMouse.Length > 0
					? iconsUnderMouse[0]
					: null;
			}
		}
		#endregion
		#endregion

		protected override void Awake()
        {
			base.Awake();
			calculator = new PathbuilderCalculator();
			segmentPool = GetComponent<SegmentPool>();
		}

		[NRListener]
		private void OnToolChanged(EditorTool tool)
        {
			if(tool == EditorTool.Pathbuilder && !isActive)
            {
				isActive = true;
				Activate(true);
            }
			else if(tool == EditorTool.None && isActive)
            {
				isActive = false;
				Activate(false);
            }
        }

		[NRListener]
		private void OnBehaviorChanged(TargetBehavior behavior)
        {
			if (isActive)
            {
				EditorState.SelectTool(EditorTool.Pathbuilder);
            }
        }

        public void Activate(bool activate)
        {
			isActive = activate;
			if (activate)
            {
				ShowUI();
				OnActivated();
				EditorState.SelectSnappingMode(SnappingMode.None);
				if(timeline.selectedNotes.Count == 1)
                {
                    if (timeline.selectedNotes[0].data.isPathbuilderTarget)
                    {
						LoadTargetData(timeline.selectedNotes[0]);
                    }
                }
				else if (timeline.selectedNotes.Count > 1)
                {
					TryStartReposition(timeline.selectedNotes);
                }
            }
			else
            {
				if (repositionMode) FinishReposition();
				EditorState.SelectSnappingMode(EditorState.Snapping.Previous);
				HideUI();
				ClearData();
				OnDeactivated();
            }
        }

		private void RemoveAllSegments()
        {
			for (int i = segments.Count - 1; i >= 0; i--)
			{
				segmentPool.Return(segments[i]);
			}
			activeSegment = null;
			segments.Clear();

			if(tempSegment != null)
            {
				segmentPool.Return(tempSegment);
				tempSegment = null;
            }
		}

        internal void OnDenominatorChanged(int denominator)
        {
			if (repositionMode) return;
            if (isSegmentScope)
            {
				activeSegment.SetDenominator(denominator);
			}
            else
            {
				intervalOverride.denominator = denominator;
            }
			UpdateActivePathbuilderTarget(activeTarget);
		}

        internal void ChangeAlternateHands()
        {
			if (repositionMode) return;
			alternateHands = !alternateHands;
			activeTarget.data.pathbuilderData.AlternateHands = alternateHands;
			UpdateActivePathbuilderTarget(activeTarget);
		}

		public void BakeActiveTarget()
        {
			if (activeTarget == null) return;
			if (repositionMode)
            {
				// "Bake" in reposition mode = apply the new positions and leave the tool.
				Activate(false);
				return;
            }
			NRActionBakePathbuilderTarget action = new NRActionBakePathbuilderTarget(activeTarget.data, this);
			timeline.Tools.undoRedoManager.AddAction(action);
        }

        internal void BakeTarget(TargetData data)
        {

			data.isPathbuilderTarget = false;
			foreach(var segment in data.pathbuilderData.Segments)
            {
				foreach(var foundTarget in timeline.FindNotes(segment.generatedNodes))
                {
					foundTarget.transient = false;
                }
            }
			OnPathbuilderTargetChanged(activeTarget);
        }

        internal void ChangeScope()
        {
			if (repositionMode) return;
			isSegmentScope = !isSegmentScope;
			activeTarget.data.pathbuilderData.IsSegmentScope = isSegmentScope;
			UpdateActivePathbuilderTarget(activeTarget);
		}

        internal void RemovePathbuilderTarget(TargetData targetData)
        {
			RemoveAllNodes(targetData.pathbuilderData);
			if(activeTarget != null && activeTarget.data == targetData)
            {
				ClearData();
				UpdateUI();
            }
			
        }

		private void Update()
        {
			if (!isMouseDown || !dragNote || activeTarget == null || activeSegment == null) return;
			var pos = NoteGridSnap.SnapToGrid(GetMousePosition(), snapToGrid ? SnappingMode.Grid : SnappingMode.None);
			activeSegment.OnHandleDragStart();
			activeTarget.data.position = pos;
        }

		private void LateUpdate()
        {
			if (!repositionMode) return;
			if (activeTarget == null || activeTarget.gridTargetIcon == null)
            {
				// Root target got deleted or unloaded while editing; bail out safely.
				CancelReposition();
				return;
            }
			ApplyRepositionPreview();
        }

        private void OnMouseClickEnd()
        {
			isMouseDown = false;
			if (!dragNote) return;
			dragNote = false;
			if (activeSegment != null) activeSegment.OnHandleDragStop();
			// In reposition mode the root's move is recorded by the single commit action.
			if (repositionMode) return;
			TargetGridMoveIntent intent = new TargetGridMoveIntent();
			intent.target = activeTarget.data;
			intent.startingPosition = dragStartPos;
			intent.intendedPosition = activeTarget.data.position;
			timeline.MoveGridTargets(new List<TargetGridMoveIntent>() { intent });
        }

       

        private Vector3 GetMousePosition()
		{
			Vector3 pos = Camera.main.ScreenToWorldPoint(actions.Pathbuilder.MousePosition.ReadValue<Vector2>());
			pos.z = 0;
			return pos;
		}

		private void LoadTargetData(Target target)
        {
            if (!target.data.isPathbuilderTarget)
            {
				MakeNewPathbuilderTarget(target);
				return;
            }
			activeTarget = target;
			timeline.DeselectAllTargets();
			timeline.SelectTarget(target);
			var data = target.data.pathbuilderData;
			var segmentData = data.Segments;
			Transform startPoint = target.gridTargetIcon.transform;
			isSegmentScope = data.IsSegmentScope;
			alternateHands = data.AlternateHands;
			for(int i = 0; i < segmentData.Count; i++)
            {
				var segment = segmentPool.Spawn(pathbuilderParent);
				segment.Initialize(this, actions);
				if(i != 0)
                {
					var lastSegment = segments.Last();
					lastSegment.childSegment = segment;
					segment.parentSegment = lastSegment;
				}
				segment.LoadSegment(this, actions, startPoint, target, segmentData[i], segments.Count);
				segments.Add(segment);
				startPoint = segment.GetSegmentEndPoint();
            }
			SetActiveSegment(segments[data.ActiveSegment]);
		}

        public void HandleRootNoteDelete(TargetData targetData)
        {
			foreach (var segment in targetData.pathbuilderData.Segments)
			{
				foreach (var node in segment.generatedNodes)
				{
					timeline.DeleteTargetFromAction(node);
				}
			}
		}

		private void SwitchData(Target target)
        {
			ClearData();
			LoadTargetData(target);
			UpdateUI();
        }

		private void UpdateUI()
        {
			if (!ui.isOpen) return;

			if(activeSegment == null)
            {
				ui.ResetPanel();
            }
            else
            {
				ui.LoadData(isSegmentScope ? activeSegment.interval : intervalOverride, isSegmentScope ? activeSegment.beatLength : beatLengthOverride, isSegmentScope, alternateHands);
			}
			
        }

		private void ShowUI()
        {
			ui.Show();
			UpdateUI();
        }

		private void HideUI()
        {
			ui.Hide();
        }


        internal void OnNominatorChanged(int nominator)
        {
			if (repositionMode) return;
            if (isSegmentScope)
            {
				activeSegment.SetNominator(nominator);
            }
            else
            {
				intervalOverride.nominator = nominator;
            }
			UpdateActivePathbuilderTarget(activeTarget);
		}

		internal void OnBeatlengthChanged(bool increase)
        {
			if (repositionMode) return;
			QNT_Duration increment = Constants.DurationFromBeatSnap((uint)timeline.beatSnap);
			QNT_Duration targetLength = isSegmentScope ? activeSegment.beatLength : beatLengthOverride;
			if (increase)
			{
				if (targetLength < increment)
				{
					targetLength = new QNT_Duration(0);
				}
				targetLength += increment;
			}
			else
			{
				if(targetLength.tick - increment.tick > 0)
                {
					targetLength -= increment;
                }
			}
			if (isSegmentScope) activeSegment.SetBeatlength(targetLength); 
			else beatLengthOverride = targetLength;			
			UpdateActivePathbuilderTarget(activeTarget);
		}

		private void MakeNewPathbuilderTarget(Target target)
        {
			activeTarget = target;
			activeTarget.data.pathbuilderData = new PathbuilderData();
			beatLengthOverride = Constants.QuarterNoteDuration;
			timeline.DeselectAllTargets();
			timeline.SelectTarget(activeTarget);
			var segment = segmentPool.Spawn(pathbuilderParent);
			segment.Initialize(this, actions);
			segment.StartNewSegment(actions, activeTarget.gridTargetIcon.transform, activeTarget, this, segments.Count);
			//segments.Add(segment);
			tempSegment = segment;
			segment.SetInterval(new PathbuilderData.Interval());
			segment.SetBeatlength(new QNT_Duration(480));
		}

        private void AppendSegment()
        {
			var segment = segmentPool.Spawn(pathbuilderParent);
			var lastSegment = segments.Last();
			lastSegment.childSegment = segment;
			segment.parentSegment = lastSegment;
			segment.StartNewSegment(actions, lastSegment.GetSegmentEndPoint(), activeTarget, this, segments.Count);
			segments.Add(segment);
			segment.SetInterval(new PathbuilderData.Interval(lastSegment.interval.nominator, lastSegment.interval.denominator));
			segment.SetBeatlength(lastSegment.beatLength);
			segment.SetSegmentEndPoint();
			SaveTargetState();
		}

		public void SaveTargetState()
        {
            if (repositionMode)
            {
				// No per-step undo in reposition mode: positions are previewed live in LateUpdate
				// and committed as one undoable action when the tool closes.
				// If every segment was removed, start a fresh one from the root again.
				if (segments.Count == 0 && tempSegment == null) StartRepositionTempSegment();
				return;
            }
			NRActionUpdatePathbuilderTarget segmentAction = new NRActionUpdatePathbuilderTarget(activeTarget.data, this, GetPathbuilderData());
			Timeline.instance.Tools.undoRedoManager.AddAction(segmentAction);
		}

		public void UpdatePathbuilderTargetFromAction(TargetData targetData, PathbuilderData data)
        {
			Target target = (activeTarget != null && activeTarget.data == targetData) ? activeTarget : timeline.FindNote(targetData);
			if (target == null) return;
			if (targetData.pathbuilderData == null) targetData.pathbuilderData = new PathbuilderData();
			else RemoveAllNodes(targetData.pathbuilderData);
			targetData.pathbuilderData = data;
			targetData.isPathbuilderTarget = data.Segments.Count > 0;
			calculator.CalculateNodes(targetData, true);
			OnPathbuilderTargetChanged(target);
            if (ui.isOpen && targetData.isPathbuilderTarget)
            {
				activeTarget = null;
				SwitchData(target);
            }
		}

		private void UpdateActivePathbuilderTarget(Target target)
        {
			if (activeTarget == null) return;
			Save();
			UpdatePathbuilderTargetFromAction(target.data, target.data.pathbuilderData);
        }

		public void UpdatePathbuilderTarget(TargetData data)
        {
			UpdatePathbuilderTargetFromAction(data, data.pathbuilderData);
        }

		public void OnPathbuilderTargetChanged(Target target)
        {
			target.timelineTargetIcon.SetBeatlengthLineActive(target.data.isPathbuilderTarget);
            if (!target.data.isPathbuilderTarget)
            {
				UpdateSegmentIndicator(target, false);
				activeTarget = null;
				target.data.pathbuilderData = null;
				ClearData();
				UpdateUI();
            }
		}

		private PathbuilderData GetPathbuilderData()
        {
			if (segments.Count == 0) return null;
			PathbuilderData data = new PathbuilderData();

			List<PathbuilderData.Segment> segmentData = new List<PathbuilderData.Segment>();
			QNT_Duration length = new QNT_Duration(0);
			foreach (var segment in segments)
            {
				segmentData.Add(segment.GetSegmentData());
				length += segment.beatLength;
            }
			data.BeatLengthOverride = beatLengthOverride;
			data.ActiveSegment = activeSegment.Index;
			data.AlternateHands = alternateHands;
			data.IsSegmentScope = isSegmentScope;
			data.Segments = segmentData;
			data.IntervalOverride = intervalOverride;
			return data;
        }
		
		/// <summary>
		/// Calculates nodes on NR load without generating them.
		/// </summary>
		/// <param name="data">The pathbuilder target's TargetData.</param>
		public void CalculateNodesOnLoad(TargetData data)
        {
			calculator.CalculateNodes(data, false);
        }
		/// <summary>
		/// Generates nodes on NR load.
		/// </summary>
		/// <param name="data">The pathbuilder target's TargetData.</param>
		public void GenerateNodesOnLoad(TargetData data)
        {
			calculator.GenerateNodes(data.pathbuilderData);
			var found = timeline.FindNote(data);
			if (found != null) found.timelineTargetIcon.SetBeatlengthLineActive(true);
        }
		/// <summary>
		/// Sets a new segment to active.
		/// </summary>
		/// <param name="segment">The segment to mark active.</param>
		internal void SetActiveSegment(Segment segment)
        {
			if (activeSegment == segment) return;
			foreach (var s in segments) s.SetSelected(false);
			segment.SetSelected(true);
			activeSegment = segment;
			if (!repositionMode) UpdateSegmentIndicator(activeTarget, true);
			UpdateUI();
        }
		/// <summary>
		/// Updates the position of the active segment indicator and enables/disables it.
		/// </summary>
		/// <param name="target">The currently selected pathbuilder target.</param>
		/// <param name="enable">True if you want to enable the indicator.</param>
		private void UpdateSegmentIndicator(Target target, bool enable)
        {
			// The target's icons can be destroyed (deleted, difficulty switch, map reload) while it's still referenced here.
			// Touching them throws, which used to abort ClearData() before activeTarget got reset.
			if (target == null || target.timelineTargetIcon == null)
            {
				activeSegmentIndicator.enabled = false;
				return;
            }
            if (enable)
            {
				float height = target.timelineTargetIcon.sustainDirection;
				QNT_Duration startTime = new QNT_Duration(0);
				QNT_Duration endTime = startTime;
                if (isSegmentScope)
                {
					foreach (var segment in segments)
					{
						if (segment == activeSegment)
						{
							endTime = startTime + segment.beatLength;
							break;
						}
						startTime += segment.beatLength;

					}
                }
                else
                {
					endTime = beatLengthOverride;
                }

				float scale = 20.0f / Timeline.scale;
				activeSegmentIndicator.enabled = true;
				activeSegmentIndicator.transform.localPosition = target.timelineTargetIcon.transform.localPosition;
				activeSegmentIndicator.SetPosition(0, new Vector3((startTime.ToBeatTime() / 0.7f) * scale * 1.75f, height, 0f));
				activeSegmentIndicator.SetPosition(1, new Vector3((endTime.ToBeatTime() / 0.7f) * scale * 1.75f, height, 0f));
				Color color = target.data.handType == TargetHandType.Left ? NRSettings.config.leftColor : NRSettings.config.rightColor;
				activeSegmentIndicator.startColor = color;
				activeSegmentIndicator.endColor = color;
				target.timelineTargetIcon.MakeSustainIndicatorTransparent(true);
			}
            else
            {
				activeSegmentIndicator.enabled = false;
				target.timelineTargetIcon.MakeSustainIndicatorTransparent(false);
            }		
        }
		/// <summary>
		/// Removes the last segment from the current path.
		/// </summary>
		public void RemoveLastSegment()
        {
			if (segments.Count == 0)
            {
				return;
            }
			var segment = segments.Last();
			segments.Remove(segment);

			if(segments.Count == 0)
            {
				activeSegment = null;
            }
			else if(activeSegment == segment)
			{
				SetActiveSegment(segments.Last());
            }

			segmentPool.Return(segment);
			SaveTargetState();
		}
		/// <summary>
		/// Removes all nodes of a path.
		/// </summary>
		/// <param name="data">The pathbuilder data you want to remove nodes for.</param>
		internal void RemoveAllNodes(PathbuilderData data)
		{
			foreach (var segment in data.Segments)
			{
				foreach (var node in segment.generatedNodes)
				{
					timeline.DeleteTargetFromAction(node);
				}
			}
		}
		/// <summary>
		/// Resets all data and UI.
		/// </summary>
		private void ClearData()
        {
			
			Save();
			RemoveAllSegments();
			//timeline.DeselectAllTargets();
			if(activeTarget != null)
            {
				UpdateSegmentIndicator(activeTarget, false);
			}
			activeTarget = null;
			activePoint = null;
			activeSegment = null;
			alternateHands = false;
			intervalOverride = new PathbuilderData.Interval();
			beatLengthOverride = Constants.QuarterNoteDuration;
			isSegmentScope = true;
        }
		/// <summary>
		/// Saves current pathbuilder data to active target.
		/// </summary>
		private void Save()
        {
			if (repositionMode) return;
			if (activeTarget != null)
			{
				var data = GetPathbuilderData();
				if (data != null) activeTarget.data.pathbuilderData = data;
			}
		}

        /// <summary>
        /// Checks if a target is a valid Pathbuilder target.
        /// </summary>
        /// <param name="target">The target to check</param>
        /// <returns>True if target is not chain, melee, mine or a NR tool or already a pathbuilder target and not a transient</returns>
        private bool IsValidPathbuilderCandidate(Target target)
        {
			if (target == null) return false;

			int index = (int)target.data.behavior;
			return index < 5 && !target.transient && !target.data.isPathbuilderTarget;
        }

		#region Reposition Mode
		/// <summary>
		/// Targets that may be moved onto a path: no melees/mines, no pathbuilder roots or generated nodes.
		/// </summary>
		private bool IsValidRepositionCandidate(Target target)
        {
			if (target == null || target.transient) return false;
			var data = target.data;
			if (data.isPathbuilderTarget) return false;
			var b = data.behavior;
			return b != TargetBehavior.Melee && b != TargetBehavior.Mine && b != TargetBehavior.Legacy_Pathbuilder && b != TargetBehavior.None;
        }

		/// <summary>
		/// Enters reposition mode with the given selection. The earliest target becomes the path start;
		/// only targets of the same hand as that start are moved.
		/// </summary>
		/// <returns>True if reposition mode was started.</returns>
		private bool TryStartReposition(List<Target> selection)
        {
			var candidates = selection.Where(IsValidRepositionCandidate).OrderBy(t => t.data.time.tick).ToList();
			if (candidates.Count < 2)
            {
				NotificationCenter.SendNotification("Pathbuilder: select at least 2 regular targets (no melees/mines/pathbuilder targets) to build a path through them.", NotificationType.Warning);
				return false;
            }

			Target root = candidates[0];
			if (root.gridTargetIcon == null) return false;
			TargetHandType hand = root.data.handType;
			var targets = candidates.Where(t => t.data.handType == hand).ToList();
			if (targets.Count < 2)
            {
				NotificationCenter.SendNotification("Pathbuilder: need at least 2 selected targets of the same hand as the earliest one.", NotificationType.Warning);
				return false;
            }
			if (targets.Last().data.time.tick == root.data.time.tick)
            {
				NotificationCenter.SendNotification("Pathbuilder: selected targets need different times to build a path.", NotificationType.Warning);
				return false;
            }

			ClearData();
			repositionMode = true;
			activeTarget = root;
			repositionTargets = targets.Select(t => t.data).ToList();
			repositionStartPositions = repositionTargets.Select(d => d.position).ToList();

			// Keep the window open (Bake applies the path). Interval / beat length controls are no-ops here:
			// timing comes from the targets themselves.
			SetPathLayerOnTop(true);
			StartRepositionTempSegment();
			UpdateUI();

			int skipped = selection.Count - targets.Count;
			string kind = root.data.behavior == TargetBehavior.ChainStart ? "chain" : "path";
			string message = $"Pathbuilder: drawing a {kind} through {targets.Count} selected targets. Click to place the end point, drag the handles to curve it, then Bake or Esc to apply.";
			if (skipped > 0) message += $" {skipped} target(s) of the other hand or an unsupported type were left alone.";
			NotificationCenter.SendNotification(message, NotificationType.Info);
			return true;
        }

		/// <summary>
		/// Starts a segment from the current path end (or the root) that follows the mouse until clicked.
		/// </summary>
		private void StartRepositionTempSegment()
        {
			if (activeTarget == null) return;
			var segment = segmentPool.Spawn(pathbuilderParent);
			segment.Initialize(this, actions);
			Transform start = segments.Count > 0 ? segments.Last().GetSegmentEndPoint() : activeTarget.gridTargetIcon.transform;
			segment.StartNewSegment(actions, start, activeTarget, this, segments.Count);
			segment.SetInterval(new PathbuilderData.Interval());
			segment.SetBeatlength(Constants.QuarterNoteDuration);
			tempSegment = segment;
        }

		/// <summary>
		/// Collects the current path as cubic bezier control points (start, startHandle, endHandle, end).
		/// A segment still following the mouse is treated as a straight line.
		/// </summary>
		private List<Vector2[]> GetRepositionCurves()
        {
			var curves = new List<Vector2[]>();
			foreach (var segment in segments)
            {
				var d = segment.GetSegmentData();
				curves.Add(new Vector2[] { d.startPoint, d.startPointHandle, d.endPointHandle, d.endPoint });
            }
			if (tempSegment != null)
            {
				var d = tempSegment.GetSegmentData();
				curves.Add(new Vector2[] { d.startPoint, Vector2.Lerp(d.startPoint, d.endPoint, .25f), Vector2.Lerp(d.startPoint, d.endPoint, .75f), d.endPoint });
            }
			return curves;
        }

		/// <summary>
		/// Moves the selected targets onto the current path. Called every frame while in reposition mode.
		/// </summary>
		private void ApplyRepositionPreview()
        {
			var curves = GetRepositionCurves();
			if (curves.Count == 0)
            {
				RestoreRepositionPositions(true);
				return;
            }
			var ticks = repositionTargets.Select(d => d.time.tick).ToList();
			var positions = PathPlacement.PlaceByTime(curves, ticks);
			// Index 0 is the root and defines the path start, so it's never moved by the path itself.
			for (int i = 1; i < repositionTargets.Count; i++)
            {
				if (repositionTargets[i].position != positions[i]) repositionTargets[i].position = positions[i];
            }
        }

		/// <summary>
		/// Puts targets back where they were when reposition mode started.
		/// </summary>
		private void RestoreRepositionPositions(bool skipRoot)
        {
			for (int i = skipRoot ? 1 : 0; i < repositionTargets.Count; i++)
            {
				if (repositionTargets[i].position != repositionStartPositions[i]) repositionTargets[i].position = repositionStartPositions[i];
            }
        }

		/// <summary>
		/// Commits the reposition as one undoable action (or reverts if no segment was placed) and leaves reposition mode.
		/// </summary>
		private void FinishReposition()
        {
			if (!repositionMode) return;
			bool hasPath = segments.Count > 0;
			if (hasPath)
            {
				ApplyRepositionPreview();
				var finalPositions = repositionTargets.Select(d => d.position).ToList();
				RestoreRepositionPositions(false);
				bool makeChain = repositionTargets[0].behavior == TargetBehavior.ChainStart;
				var action = new NRActionRepositionTargets(repositionTargets, repositionStartPositions, finalPositions, makeChain);
				timeline.Tools.undoRedoManager.AddAction(action);
            }
            else
            {
				RestoreRepositionPositions(false);
            }
			EndReposition();
        }

		private void CancelReposition()
        {
			if (!repositionMode) return;
			RestoreRepositionPositions(false);
			EndReposition();
        }

		/// <summary>
		/// The path canvas sorts behind targets (order -3). In reposition mode the real targets sit right on the line
		/// and cover the handles, so the path layer gets its own canvas sorted above them while editing.
		/// </summary>
		private void SetPathLayerOnTop(bool onTop)
        {
			var layer = pathbuilderParent.GetComponent<Canvas>();
			if (layer == null)
            {
				if (!onTop) return;
				layer = pathbuilderParent.gameObject.AddComponent<Canvas>();
				// A nested canvas needs its own raycaster, otherwise the handles stop receiving clicks.
				pathbuilderParent.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            }
			layer.overrideSorting = onTop;
			layer.sortingOrder = onTop ? 100 : 0;
        }

		private void EndReposition()
        {
			SetPathLayerOnTop(false);
			RemoveAllSegments();
			activeTarget = null;
			activePoint = null;
			repositionMode = false;
			repositionTargets = new List<TargetData>();
			repositionStartPositions = new List<Vector2>();
        }
		#endregion

		#region Input Callbacks
		protected override void RegisterCallbacks()
		{
			actions.Pathbuilder.SelectTarget.performed += _ => OnMouseClick();
			actions.Pathbuilder.SelectTarget.canceled += _ => OnMouseClickEnd();
			actions.Pathbuilder.AppendSegment.performed += _ => OnAppendSegment();
			actions.Pathbuilder.DeleteSegment.performed += _ => RemoveLastSegment();
			actions.Pathbuilder.SnapToGrid.performed += _ => OnShiftDown(true);
			actions.Pathbuilder.SnapToGrid.canceled += _ => OnShiftDown(false);

			actions.Pathbuilder.AlternateHands.performed += _ => OnAlternateHandsPressed();
			actions.Pathbuilder.ChangeScope.performed += _ => OnChangeScopePressed();
			actions.Pathbuilder.ChangeToNextSegment.performed += _ => OnChangeActiveSegment(true);
			actions.Pathbuilder.ChangeToPreviousSegment.performed += _ => OnChangeActiveSegment(false);
			actions.Pathbuilder.IncreaseInterval.performed += _ => OnChangeInterval(true);
			actions.Pathbuilder.DecreaseInterval.performed += _ => OnChangeInterval(false);
			actions.Pathbuilder.IncreaseLength.performed += _ => OnChangeLength(true);
			actions.Pathbuilder.DecreaseLength.performed += _ => OnChangeLength(false);		
		}

		private void OnMouseClick()
		{
			iconsUnderMouse = null;
			isMouseDown = true;

            if (repositionMode)
            {
				// Clicking the root lets you drag the whole path's start point.
				if (iconUnderMouse != null && iconUnderMouse.target == activeTarget && segments.Count > 0)
                {
					SetActiveSegment(segments[0]);
					dragStartPos = GetMousePosition();
					dragNote = true;
					return;
                }
				// Other targets are ignored so a click never switches away from the selection.
				if (tempSegment != null)
                {
					tempSegment.SetSegmentEndPoint();
					segments.Add(tempSegment);
					tempSegment = null;
                }
				return;
            }

			if (iconUnderMouse != null)
			{
				var target = iconUnderMouse.target;

				if (target == activeTarget)
				{
					if (segments.Count > 0)
					{
						SetActiveSegment(segments[0]);
					}
					dragStartPos = GetMousePosition();
					dragNote = true;
					return;
				}
				else if (target.data.isPathbuilderTarget)
				{
					if (activeTarget != target)
					{
						SwitchData(target);
						return;
					}
				}
				else if (IsValidPathbuilderCandidate(target))
				{
					SwitchData(target);
					return;
				}
			}

			if (tempSegment != null)
			{
				tempSegment.SetSegmentEndPoint();
				segments.Add(tempSegment);
				tempSegment = null;
				SaveTargetState();
			}
		}

		/// <summary>
		/// Callback for when a point in a segment is clicked.
		/// </summary>
		/// <param name="segment">The segment the point belongs to.</param>
		/// <param name="point">The point registering a click.</param>
		public void OnPointClicked(Segment segment, Point point)
		{
			activePoint = point;
			point.ShouldSnap(snapToGrid);
			if (KeybindManager.Global.Modifier == KeybindManager.Global.Modifiers.None)
			{
				SetActiveSegment(segment);
			}
		}

		private void OnShiftDown(bool down)
        {
			snapToGrid = down;
			if (activePoint != null)
			{
				activePoint.ShouldSnap(down);
			}
        }
		private void OnAppendSegment()
        {
			if (activeTarget == null || activeSegment == null) return;			
			AppendSegment();
		}

		private void OnAlternateHandsPressed()
        {
			if (activeTarget == null || activeSegment == null) return;			
			ChangeAlternateHands();        
        }

		private void OnChangeScopePressed()
        {
			if (activeTarget == null || activeSegment == null) return;		
			ChangeScope();
        }

		private void OnChangeActiveSegment(bool next)
        {
			if (activeTarget == null || activeSegment == null) return;
			int index = activeSegment.Index + (next ? 1 : -1);
			if(index >= 0 && index < segments.Count)
            {
				SetActiveSegment(segments[index]);
            }
        }

		private void OnChangeInterval(bool increase)
        {
			if (activeTarget == null || activeSegment == null || repositionMode) return;
			ui.OnIntervalChanged(increase);
        }

		private void OnChangeLength(bool increase)
        {
			if (activeTarget == null || activeSegment == null) return;
			OnBeatlengthChanged(increase);
        }

		protected override void OnEscPressed(InputAction.CallbackContext context)
		{
			Activate(false);
		}

        protected override void SetRebindConfiguration(ref RebindConfiguration options, PathbuilderKeybinds myKeybinds)
        {
			options.SetAssetTitle("Pathbuilder").SetPriority(20);
			options.AddCustomKeybindName(myKeybinds.Pathbuilder.ChangeToNextSegment, "Next Segment").AddCustomKeybindName(myKeybinds.Pathbuilder.ChangeToPreviousSegment, "Previous Segment");
			options.AddHiddenKeybinds(myKeybinds.Pathbuilder.MousePosition);
			options.AddNonRebindableKeybinds(myKeybinds.Pathbuilder.SelectTarget);
			options.AddNonRebindableKeybinds(myKeybinds.Pathbuilder.SnapToGrid);
        }
        #endregion
    }

	/// <summary>
	/// Places targets along a chain of cubic bezier curves according to their time.
	/// The first target sits at the path start, the last at the path end, and everything in between
	/// is placed by arc length, so the path is travelled at constant speed and gaps in timing
	/// (e.g. a missing 4th beat) show up as matching gaps in space.
	/// </summary>
	public static class PathPlacement
    {
		public const int SamplesPerCurve = 64;

		public static Vector2 Cubic(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
			float u = 1f - t;
			return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
        }

		/// <param name="curves">Each entry is {start, startHandle, endHandle, end}; curve i+1 starts where curve i ends.</param>
		/// <param name="ticks">Target times, sorted ascending.</param>
		/// <returns>One position per tick.</returns>
		public static List<Vector2> PlaceByTime(List<Vector2[]> curves, IList<ulong> ticks, int samplesPerCurve = SamplesPerCurve)
        {
			var result = new List<Vector2>(ticks.Count);
			if (ticks.Count == 0) return result;
			if (curves == null || curves.Count == 0)
            {
				for (int i = 0; i < ticks.Count; i++) result.Add(Vector2.zero);
				return result;
            }

			// Sample the whole path and build a cumulative arc length table.
			var points = new List<Vector2>(curves.Count * samplesPerCurve + 1);
			var lengths = new List<float>(curves.Count * samplesPerCurve + 1);
			points.Add(curves[0][0]);
			lengths.Add(0f);
			foreach (var c in curves)
            {
				for (int s = 1; s <= samplesPerCurve; s++)
                {
					Vector2 p = Cubic(c[0], c[1], c[2], c[3], (float)s / samplesPerCurve);
					lengths.Add(lengths[lengths.Count - 1] + Vector2.Distance(points[points.Count - 1], p));
					points.Add(p);
                }
            }

			float total = lengths[lengths.Count - 1];
			ulong first = ticks[0];
			ulong last = ticks[ticks.Count - 1];
			double span = last > first ? (double)(last - first) : 0d;

			int seg = 0;
			for (int i = 0; i < ticks.Count; i++)
            {
				if (total <= Mathf.Epsilon || span <= 0d)
                {
					result.Add(points[0]);
					continue;
                }
				float f = (float)((ticks[i] - first) / span);
				float targetLength = Mathf.Clamp01(f) * total;
				// ticks are sorted, so the sample index only ever moves forward.
				while (seg < lengths.Count - 2 && lengths[seg + 1] < targetLength) seg++;
				float segLength = lengths[seg + 1] - lengths[seg];
				float t = segLength > Mathf.Epsilon ? (targetLength - lengths[seg]) / segLength : 0f;
				result.Add(Vector2.Lerp(points[seg], points[seg + 1], t));
            }
			return result;
        }
    }
}