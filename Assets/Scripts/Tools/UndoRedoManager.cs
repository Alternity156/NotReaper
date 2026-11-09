using System.Linq;
using System.Collections.Generic;
using NotReaper.Grid;
using NotReaper.Targets;
using NotReaper.UserInput;
using NotReaper.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using NotReaper.Managers;
using NotReaper.Timing;
using System;
using NotReaper.Tools.PathBuilder;

namespace NotReaper.Tools {


	public class UndoRedoManager : MonoBehaviour {

		/// <summary>
		/// Contains the complete list of actions the user has done recently.
		/// </summary>
		public List<NRAction> actions = new List<NRAction>();

		/// <summary>
		/// Contains the actions the user has "undone" for future use.
		/// </summary>
		public List<NRAction> redoActions = new List<NRAction>();

		public int maxSavedActions = 20;

		public Timeline timeline;

		public void Undo() {
			if (actions.Count <= 0) return;

			NRAction action = actions.Last();

			action.UndoAction(timeline);

			redoActions.Add(action);
			actions.RemoveAt(actions.Count - 1);

			timeline.ReapplyScale();
		}

		public void Redo() {

			if (redoActions.Count <= 0) return;

			NRAction action = redoActions.Last();

			action.DoAction(timeline);

			actions.Add(action);
			redoActions.RemoveAt(redoActions.Count - 1);
			timeline.ReapplyScale();
		}

		public void AddAction(NRAction action) {
			action.DoAction(timeline);
			if (actions.Count <= maxSavedActions) {
				actions.Add(action);
			} else {
				while (maxSavedActions > actions.Count) {
					actions.RemoveAt(0);
				}

				actions.Add(action);
			}

			redoActions = new List<NRAction>();
		}

		public void ClearActions() {
			actions = new List<NRAction>();
			redoActions = new List<NRAction>();
		}
	}

	public abstract class NRAction {
		public abstract void DoAction(Timeline timeline);
		public abstract void UndoAction(Timeline timeline);
	}

	public class NRActionAddNote : NRAction {
		public TargetData targetData;
		public List<TargetData> repeaterData;

		public override void DoAction(Timeline timeline) {
			timeline.AddTargetFromAction(targetData);
			if (repeaterData == null) {
				repeaterData = timeline.GenerateRepeaterTargets(targetData);
			}
			repeaterData.ForEach(data => { timeline.AddTargetFromAction(data); });
		}
		public override void UndoAction(Timeline timeline) {
			timeline.DeleteTargetFromAction(targetData);
			repeaterData.ForEach(data => { timeline.DeleteTargetFromAction(data); });
		}
	}

	public class NRActionMultiAddNote : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();
		public List<NRActionAddNote> actions;

		public override void DoAction(Timeline timeline) {
			if (actions == null) {
				actions = affectedTargets.Select(targetData => { var action = new NRActionAddNote(); action.targetData = targetData; return action; }).ToList();
				affectedTargets = null;
			}
			actions.ForEach(action => { action.DoAction(timeline); });
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			actions.ForEach(action => { action.UndoAction(timeline); });
			TransformTool.instance.UpdateOverlay();
		}
	}

	public class NRActionRemoveNote : NRAction {
		public TargetData targetData;
		public List<TargetData> repeaterData;

		public override void DoAction(Timeline timeline) {
			if (repeaterData == null) {
				repeaterData = timeline.FindRepeaterTargets(targetData);
			}
            if (targetData.isPathbuilderTarget)
            {
				timeline.pathbuilder.RemovePathbuilderTarget(targetData);
            }
			timeline.DeleteTargetFromAction(targetData);
			repeaterData.ForEach(data => { timeline.DeleteTargetFromAction(data); });
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			timeline.AddTargetFromAction(targetData);
			repeaterData.ForEach(data => { timeline.AddTargetFromAction(data); });

			TransformTool.instance.UpdateOverlay();
		}
	}

	public class NRActionMultiRemoveNote : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();
		public List<NRActionRemoveNote> actions;

		public override void DoAction(Timeline timeline) {
			if (actions == null) {
				actions = affectedTargets.Select(targetData => { var action = new NRActionRemoveNote(); action.targetData = targetData; return action; }).ToList();
				affectedTargets = null;
			}

			actions.ForEach(action => { action.DoAction(timeline); });
		}
		public override void UndoAction(Timeline timeline) {
			actions.ForEach(action => { action.UndoAction(timeline); });
		}
	}

	public class NRActionGridMoveNotes : NRAction {
		public List<TargetGridMoveIntent> targetGridMoveIntents = new List<TargetGridMoveIntent>();

		public override void DoAction(Timeline timeline) {
			targetGridMoveIntents.ForEach(intent => 
			{
                
				intent.target.position = intent.intendedPosition;

			});
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			targetGridMoveIntents.ForEach(intent => 
			{
                
				intent.target.position = intent.startingPosition;
                
			});
			TransformTool.instance.UpdateOverlay();
		}
	}

	public class NRActionTimelineMoveNotes : NRAction {
		public List<TargetTimelineMoveIntent> targetTimelineMoveIntents = new List<TargetTimelineMoveIntent>();

		public override void DoAction(Timeline timeline) {
			//First, we destroy all siblings (either because we moved out of a repeater, or because we moved too far into a repeater that another section didn't cover)
			targetTimelineMoveIntents.ForEach(intent => {
				intent.startSiblingsToBeDestroyed.ForEach(data => { timeline.DeleteTargetFromAction(data); });
			});

			targetTimelineMoveIntents.ForEach(intent => {
				//Move the actual note
				intent.targetData.SetTimeFromAction(intent.intendedTick);

				//Then, we move all the siblings by the delta
				intent.startSiblingsToBeMoved.ForEach(sibling => { sibling.SetTimeFromAction(sibling.time + (intent.intendedTick - intent.startTick)); });

				//Finally, create targets in the ending section (if any exist)
				intent.endRepeaterSiblingsToBeCreated.ForEach(data => { timeline.AddTargetFromAction(data); });
			});
			timeline.SortOrderedList();
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			//First, destroy targets in the ending section (if any exist)
			targetTimelineMoveIntents.ForEach(intent => {
				intent.endRepeaterSiblingsToBeCreated.ForEach(data => { timeline.DeleteTargetFromAction(data); });
			});

			targetTimelineMoveIntents.ForEach(intent => {
				//Then, we move all the siblings by the delta
				intent.startSiblingsToBeMoved.ForEach(sibling => { sibling.SetTimeFromAction(sibling.time + (intent.startTick - intent.intendedTick)); });

				//First, we move the actual note
				intent.targetData.SetTimeFromAction(intent.startTick);

				//Next, we create all siblings (either because we moved out of a repeater, or because we moved too far into a repeater that another section didn't cover)
				intent.startSiblingsToBeDestroyed.ForEach(data => { timeline.AddTargetFromAction(data); });
			});
			timeline.SortOrderedList();
			TransformTool.instance.UpdateOverlay();
		}
	}

	public class NRActionSwapNoteColors : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();

		public override void DoAction(Timeline timeline) {
			affectedTargets.ForEach(targetData => {
				switch (targetData.handType) {
					case TargetHandType.Left:
						targetData.handType = TargetHandType.Right;
						break;

					case TargetHandType.Right:
						targetData.handType = TargetHandType.Left;
						break;
				}

				if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
					switch (targetData.legacyPathbuilderData.handType) {
						case TargetHandType.Left:
							targetData.legacyPathbuilderData.handType = TargetHandType.Right;
							break;

						case TargetHandType.Right:
							targetData.legacyPathbuilderData.handType = TargetHandType.Left;
							break;
					}

					targetData.handType = targetData.handType;
					ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
				}
			});
		}
		public override void UndoAction(Timeline timeline) {
			DoAction(timeline); //Swap is symmetrical
		}
	}

	public class NRActionHFlipNotes : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();

		public float FlipAngle(float angle) {
			angle = ((angle + 180) % 360) - 180;
			return -angle;
		}

		public override void DoAction(Timeline timeline) {
			affectedTargets.ForEach(targetData => {
				targetData.x *= -1;
                if (targetData.isPathbuilderTarget)
                {
					targetData.pathbuilderData.Flip(new Vector2(-1, 1));
                }
				if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
					targetData.legacyPathbuilderData.initialAngle = FlipAngle(targetData.legacyPathbuilderData.initialAngle);
					targetData.legacyPathbuilderData.angle *= -1;
					targetData.legacyPathbuilderData.angleIncrement *= -1;

					ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
				}
			});
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			DoAction(timeline); //Swap is symmetrical
		}
	}

	public class NRActionVFlipNotes : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();

		public float FlipAngle(float angle) {
			angle = ((angle + 180) % 360) - 180;

			if (angle >= 0)
				return 180 - angle;
			else
				return -180 - angle;
		}

		public override void DoAction(Timeline timeline) {
			affectedTargets.ForEach(targetData => {
				targetData.y *= -1;
                if (targetData.isPathbuilderTarget)
                {
					targetData.pathbuilderData.Flip(new Vector2(1, -1));
                }
				if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
					targetData.legacyPathbuilderData.initialAngle = FlipAngle(targetData.legacyPathbuilderData.initialAngle);
					targetData.legacyPathbuilderData.angle *= -1;
					targetData.legacyPathbuilderData.angleIncrement *= -1;

					ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
				}
			});
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			DoAction(timeline); //Swap is symmetrical
		}
	}

	public class NRActionScale : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();

		public Vector2 scale;

		public override void DoAction(Timeline timeline) {
			affectedTargets.ForEach(targetData => {
				if (targetData.behavior != TargetBehavior.Melee) {
                    if (!targetData.isPathbuilderTarget)
                    {
						targetData.y *= scale.y;
						targetData.x *= scale.x;
					}

					if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
						targetData.legacyPathbuilderData.stepDistance *= scale.x;

						ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
					}
				}
			});
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			affectedTargets.ForEach(targetData => {
				if (targetData.behavior != TargetBehavior.Melee) {
                    if (!targetData.isPathbuilderTarget)
                    {
						targetData.y /= scale.y;
						targetData.x /= scale.x;
					}
					
					if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
						targetData.legacyPathbuilderData.stepDistance /= scale.x;

						ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
					}
				}
			});
			TransformTool.instance.UpdateOverlay();
		}
	}

	public class NRActionRotate : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();

		public float rotateAngle = 0;

		public Vector2 rotateCenter = Vector2.zero;

		public void NRRotate(TargetData data, Vector2 center, float angle) {
			data.x -= center.x;
			data.y -= center.y;
			angle = -angle;

			Vector2 rotate;

			rotate.x = (float)(data.x * Math.Cos(angle / 180f * Math.PI) + data.y * Math.Sin(angle / 180f * Math.PI));
			rotate.y = (float)(data.x * -Math.Sin(angle / 180f * Math.PI) + data.y * Math.Cos(angle / 180f * Math.PI));
			rotate.x += center.x;
			rotate.y += center.y;

			data.x = rotate.x;
			data.y = rotate.y;
		}
		public override void DoAction(Timeline timeline) {
			affectedTargets.ForEach(targetData => {
				if (targetData.behavior != TargetBehavior.Melee) {
					NRRotate(targetData, rotateCenter, rotateAngle);
					if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
						targetData.legacyPathbuilderData.initialAngle -= rotateAngle;

						ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
					}
				}
			});
			TransformTool.instance.UpdateOverlay();
		}
		public override void UndoAction(Timeline timeline) {
			affectedTargets.ForEach(targetData => {
				if (targetData.behavior != TargetBehavior.Melee) {
					NRRotate(targetData, rotateCenter, -rotateAngle);
					if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
						targetData.legacyPathbuilderData.initialAngle += rotateAngle;

						ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
					}
				}
			});
			TransformTool.instance.UpdateOverlay();
		}
	}


	public class NRActionReverse : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();
		NRActionTimelineMoveNotes moveAction;

		public override void DoAction(Timeline timeline) {
			if (moveAction == null) {
				bool first = true;

				ulong firstTick = 0, lastTick = 0;

				//Find the first and last note in the sequence
				foreach (TargetData data in affectedTargets) {

					if (first) {
						firstTick = data.time.tick;
						lastTick = data.time.tick;
						first = false;
					}

					else if (data.time.tick > lastTick) {
						lastTick = data.time.tick;
					}

					else if (data.time.tick < firstTick) {
						firstTick = data.time.tick;
					}

				}

				List<TargetTimelineMoveIntent> intents = new List<TargetTimelineMoveIntent>();
				foreach (TargetData data in affectedTargets) {
					ulong amt = data.time.tick - firstTick;
					TargetTimelineMoveIntent intent = new TargetTimelineMoveIntent();
					intent.targetData = data;
					intent.startTick = data.time;
					intent.intendedTick = new QNT_Timestamp(lastTick - amt);
					intents.Add(intent);
				}

				moveAction = timeline.GenerateMoveTimelineAction(intents);
			}

			moveAction.DoAction(timeline);
		}
		public override void UndoAction(Timeline timeline) {
			moveAction.UndoAction(timeline);
		}
	}

	public class NRActionSetTargetHitsound : NRAction {
		public List<TargetSetHitsoundIntent> targetSetHitsoundIntents = new List<TargetSetHitsoundIntent>();

		public override void DoAction(Timeline timeline) {
			targetSetHitsoundIntents.ForEach(intent => {
				intent.target.velocity = intent.newVelocity;
				if (intent.target.isPathbuilderTarget)
				{
					intent.target.data.pathbuilderData.SetHitsound(intent.newVelocity);
				}
			});
		}
		public override void UndoAction(Timeline timeline) {
			targetSetHitsoundIntents.ForEach(intent => {
				intent.target.velocity = intent.startingVelocity;
				if (intent.target.isPathbuilderTarget)
				{
					intent.target.data.pathbuilderData.SetHitsound(intent.startingVelocity);
				}
			});
		}
	}

	public class NRActionSetTargetBehavior : NRAction {
		public List<TargetData> affectedTargets = new List<TargetData>();
		public TargetBehavior newBehavior;

		List<TargetBehavior> oldBehavior = new List<TargetBehavior>();
		List<TargetHandType> oldHandTypes = new List<TargetHandType>();
		List<InternalTargetVelocity> oldVelocities = new List<InternalTargetVelocity>();
		List<QNT_Duration> oldBeatLength = new List<QNT_Duration>();

		public override void DoAction(Timeline timeline) {
			oldBehavior = new List<TargetBehavior>();

			affectedTargets.ForEach(targetData => {
				InternalTargetVelocity velocity = InternalTargetVelocity.Silent;

				if (newBehavior == TargetBehavior.ChainStart) {
					velocity = InternalTargetVelocity.ChainStart;
				}
				else if (newBehavior == TargetBehavior.ChainNode) {
					velocity = InternalTargetVelocity.Chain;
				}
				else if (newBehavior == TargetBehavior.Melee) {
					velocity = InternalTargetVelocity.Melee;
				}
				else if (newBehavior == TargetBehavior.Mine) {
					velocity = InternalTargetVelocity.Mine;
				}
				else if (newBehavior == TargetBehavior.Standard ||
						newBehavior == TargetBehavior.Sustain ||
						newBehavior == TargetBehavior.Horizontal ||
						newBehavior == TargetBehavior.Vertical) {
					velocity = InternalTargetVelocity.Kick;
				}

				//Path notes and regular notes both use the same beat length
				oldBeatLength.Add(targetData.beatLength);

				if (targetData.behavior == TargetBehavior.Legacy_Pathbuilder) {
					oldBehavior.Add(targetData.legacyPathbuilderData.behavior);
					oldHandTypes.Add(targetData.legacyPathbuilderData.handType);
					oldVelocities.Add(targetData.legacyPathbuilderData.velocity);

					targetData.legacyPathbuilderData.behavior = newBehavior;
					if (velocity != InternalTargetVelocity.Silent) targetData.legacyPathbuilderData.velocity = velocity;

					//Fix hand type when going to melee
					if (newBehavior == TargetBehavior.Melee) {
						targetData.legacyPathbuilderData.handType = TargetHandType.Either;
					}

					//Fixup hand type when coming from melee
					if (oldBehavior.Last() == TargetBehavior.Melee) {
						targetData.legacyPathbuilderData.handType = targetData.handType;
					}

					ChainBuilder.ChainBuilder.GenerateChainNotes(targetData);
				}
				else {
					oldBehavior.Add(targetData.behavior);
					oldHandTypes.Add(targetData.handType);
					oldVelocities.Add(targetData.velocity);

					//if (velocity != InternalTargetVelocity.Silent) targetData.velocity = velocity;
					if (newBehavior.IsMeleeOrMine()) targetData.velocity = velocity;
					targetData.behavior = newBehavior;


					if (targetData.isPathbuilderTarget)
					{
						targetData.pathbuilderData.SetBehavior(newBehavior);
						if (velocity != InternalTargetVelocity.Silent) targetData.pathbuilderData.SetHitsound(velocity);
					}

					//Fix hand type when going to melee
					if (newBehavior == TargetBehavior.Melee) {
						targetData.handType = TargetHandType.Either;
					}

					//Fixup hand type when coming from melee
					if (oldBehavior.Last() == TargetBehavior.Melee) {
						targetData.handType = TargetHandType.Left;
					}

					if (TargetData.BehaviorSupportsBeatLength(newBehavior, targetData.isPathbuilderTarget) && targetData.beatLength < Constants.QuarterNoteDuration) {
						targetData.beatLength = Constants.QuarterNoteDuration;
					}
				}
			});
		}
		public override void UndoAction(Timeline timeline) {
			for (int i = 0; i < affectedTargets.Count; ++i) {
				if (affectedTargets[i].behavior == TargetBehavior.Legacy_Pathbuilder) {
					affectedTargets[i].legacyPathbuilderData.behavior = oldBehavior[i];
					affectedTargets[i].legacyPathbuilderData.velocity = oldVelocities[i];
					affectedTargets[i].legacyPathbuilderData.handType = oldHandTypes[i];
				}
				else {
					affectedTargets[i].behavior = oldBehavior[i];
					affectedTargets[i].handType = oldHandTypes[i];
					affectedTargets[i].velocity = oldVelocities[i];
					if (affectedTargets[i].isPathbuilderTarget)
					{
						affectedTargets[i].pathbuilderData.SetBehavior(oldBehavior[i]);
						affectedTargets[i].pathbuilderData.SetHitsound(oldVelocities[i]);
					}
				}

				affectedTargets[i].beatLength = oldBeatLength[i];
			}
		}
	}

    public class NRActionDeselectBehavior : NRAction
    {
		public TargetBehavior behaviorToDeselect;
		Target[] deselectedTargets;
        public override void DoAction(Timeline timeline)
        {
			deselectedTargets = timeline.selectedNotes
								.Where(target => target.data.behavior == behaviorToDeselect)
								.ToArray();
            foreach (var target in deselectedTargets)
            {
				target.Deselect();
                timeline.selectedNotes.Remove(target);
            }
			TransformTool.instance.UpdateOverlay();
		}

        public override void UndoAction(Timeline timeline)
        {
            foreach (var target in deselectedTargets)
			{
				target.Select();
                timeline.selectedNotes.Add(target);
            }
			TransformTool.instance.UpdateOverlay();
		}
    }

    public class NRActionDeselectHand : NRAction
    {
		public TargetHandType handToDeselect;
		Target[] deselectedTargets;
        public override void DoAction(Timeline timeline)
        {
			deselectedTargets = timeline.selectedNotes.Where(target => target.data.handType == handToDeselect).ToArray();
			foreach(var target in deselectedTargets)
            {
				target.Deselect();
				timeline.selectedNotes.Remove(target);
            }
			TransformTool.instance.UpdateOverlay();
        }

        public override void UndoAction(Timeline timeline)
        {
            foreach(var target in deselectedTargets)
            {
				target.Select();
				timeline.selectedNotes.Add(target);
            }
			TransformTool.instance.UpdateOverlay();
        }
    }

	/// <summary>
	/// Moves existing targets onto a pathbuilder path (keeping their timing).
	/// If makeChain is set, every target after the first becomes a chain node of the first target's hand.
	/// </summary>
	public class NRActionRepositionTargets : NRAction
    {
		private List<TargetData> targets;
		private List<Vector2> oldPositions;
		private List<Vector2> newPositions;
		private bool makeChain;

		private List<TargetBehavior> oldBehaviors;
		private List<InternalTargetVelocity> oldVelocities;
		private List<TargetHandType> oldHands;

		public NRActionRepositionTargets(List<TargetData> targets, List<Vector2> oldPositions, List<Vector2> newPositions, bool makeChain)
        {
			this.targets = new List<TargetData>(targets);
			this.oldPositions = new List<Vector2>(oldPositions);
			this.newPositions = new List<Vector2>(newPositions);
			this.makeChain = makeChain;
			oldBehaviors = targets.Select(t => t.behavior).ToList();
			oldVelocities = targets.Select(t => t.velocity).ToList();
			oldHands = targets.Select(t => t.handType).ToList();
        }

		public override void DoAction(Timeline timeline)
        {
			for (int i = 0; i < targets.Count; i++)
            {
				targets[i].position = newPositions[i];
				if (makeChain && i > 0)
                {
					targets[i].behavior = TargetBehavior.ChainNode;
					targets[i].velocity = InternalTargetVelocity.Chain;
					targets[i].handType = oldHands[0];
                }
            }
			TransformTool.instance.UpdateOverlay();
        }

		public override void UndoAction(Timeline timeline)
        {
			for (int i = 0; i < targets.Count; i++)
            {
				targets[i].position = oldPositions[i];
				if (makeChain && i > 0)
                {
					targets[i].behavior = oldBehaviors[i];
					targets[i].velocity = oldVelocities[i];
					targets[i].handType = oldHands[i];
                }
            }
			TransformTool.instance.UpdateOverlay();
        }
    }

	public class NRActionUpdatePathbuilderTarget : NRAction
    {
		private TargetData targetData;
		private Pathbuilder pathbuilder;

		private PathbuilderData oldState;
		private PathbuilderData newState;

		public NRActionUpdatePathbuilderTarget(TargetData targetData, Pathbuilder pathbuilder, PathbuilderData data)
		{
			this.targetData = targetData;
			this.newState = data;
			this.oldState = targetData.pathbuilderData;
			this.pathbuilder = pathbuilder;
		}
		public override void DoAction(Timeline timeline)
		{
			pathbuilder.UpdatePathbuilderTargetFromAction(targetData, newState);
		}

		public override void UndoAction(Timeline timeline)
		{
			pathbuilder.UpdatePathbuilderTargetFromAction(targetData, oldState);
		}
	}

    public class NRActionBakePathbuilderTarget : NRAction
    {
		private TargetData targetData;
		private Pathbuilder pathbuilder;
		private PathbuilderData oldState;

		public NRActionBakePathbuilderTarget(TargetData targetData, Pathbuilder pathbuilder)
        {
			this.targetData = targetData;
			this.pathbuilder = pathbuilder;
			this.oldState = targetData.pathbuilderData;
        }

		public override void DoAction(Timeline timeline)
        {
			pathbuilder.BakeTarget(targetData);
        }

        public override void UndoAction(Timeline timeline)
        {
			foreach(var segment in oldState.Segments)
            {
				foreach(var node in segment.generatedNodes)
                {
					timeline.DeleteTargetFromAction(node);
                }
            }
			pathbuilder.UpdatePathbuilderTargetFromAction(targetData, oldState);
        }
    }

    public class NRActionConvertNoteToLegacyPathbuilder : NRAction {
		public TargetData data;
		public QNT_Duration oldBeatLength;
		public LegacyPathbuilderData pathBuilderData = new LegacyPathbuilderData();

		public override void DoAction(Timeline timeline) {
			pathBuilderData.behavior = data.behavior;
			pathBuilderData.velocity = data.velocity;
			pathBuilderData.handType = data.handType;
			data.legacyPathbuilderData = pathBuilderData;

			//Ensure the path builder always starts with a quarter note of build time
			oldBeatLength = data.beatLength;
			if (data.beatLength < Constants.QuarterNoteDuration) {
				data.beatLength = Constants.QuarterNoteDuration;
			}

			data.behavior = TargetBehavior.Legacy_Pathbuilder;
			ChainBuilder.ChainBuilder.GenerateChainNotes(data);
		}
		public override void UndoAction(Timeline timeline) {
			data.legacyPathbuilderData.DeleteCreatedNotes(timeline);

			data.behavior = data.legacyPathbuilderData.behavior;
			data.velocity = data.legacyPathbuilderData.velocity;
			data.handType = data.legacyPathbuilderData.handType;
			data.beatLength = oldBeatLength;
			data.legacyPathbuilderData = null;
		}
	}

	public class NRActionBakePath : NRAction {
		public NRActionRemoveNote removeNoteAction;

		public override void DoAction(Timeline timeline) {
			//Generate and create real notes
			ChainBuilder.ChainBuilder.GenerateChainNotes(removeNoteAction.targetData);
			foreach (TargetData genData in removeNoteAction.targetData.legacyPathbuilderData.generatedNotes) {
				timeline.AddTargetFromAction(new TargetData(genData));
			}

			//Destroy the path builder note (and all the generated transient notes
			removeNoteAction.DoAction(timeline);
		}
		public override void UndoAction(Timeline timeline) {
			removeNoteAction.UndoAction(timeline);

			//Recalculate the notes, and remove the "real" notes
			ChainBuilder.ChainBuilder.CalculateChainNotes(removeNoteAction.targetData);
			foreach (TargetData genData in removeNoteAction.targetData.legacyPathbuilderData.generatedNotes) {
				var foundData = timeline.FindTargetData(genData.time, genData.behavior, genData.handType);
				if (foundData != null) {
					timeline.DeleteTargetFromAction(foundData);
				}
			}
			ChainBuilder.ChainBuilder.GenerateChainNotes(removeNoteAction.targetData);
		}
	}

	public class NRActionAddRepeaterSection : NRAction {
		public RepeaterSection section;
		public NRActionMultiAddNote addTargets = new NRActionMultiAddNote();
		public NRActionMultiRemoveNote removeTargets = new NRActionMultiRemoveNote();

		public override void DoAction(Timeline timeline) {
			addTargets.DoAction(timeline);
			removeTargets.DoAction(timeline);
			timeline.AddRepeaterSectionFromAction(section);
		}

		public override void UndoAction(Timeline timeline) {
			timeline.RemoveRepeaterSectionFromAction(section);
			addTargets.UndoAction(timeline);
			removeTargets.UndoAction(timeline);
		}
	};

	public class NRActionRemoveRepeaterSection : NRAction {
		public RepeaterSection section;

		public override void DoAction(Timeline timeline) {
			timeline.RemoveRepeaterSectionFromAction(section);
		}

		public override void UndoAction(Timeline timeline) {
			timeline.AddRepeaterSectionFromAction(section);
		}
	};
}