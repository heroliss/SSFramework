using System;
using System.Collections.Generic;
using Game.NomadWorkshop.Simulation;
using UnityEngine;

namespace Game.NomadWorkshop
{
    /// <summary>
    /// 居民 Utility AI 的可丢弃灰盒展示器：程序化生成固定俯视角 3D 甲板、一个居民和六类行动，
    /// 把纯 C# 决策结果驱动为移动与交互。Humanoid 只作为可替换表现接缝；寻路、正式设施和 UI 不属于此组件职责。
    /// </summary>
    public sealed class UtilityAiSpikeController : MonoBehaviour
    {
        private const ulong ResidentId = 0xADA01UL;
        private const int NeedCount = (int)ResidentNeed.Count;

        [SerializeField, Min(0.1f)] private float simulationSpeed = 4f;
        [SerializeField, Min(0.1f)] private float residentMoveSpeed = 2.2f;
        [SerializeField] private int worldSeed = 20260901;
        [SerializeField] private GameObject humanoidPrefab;
        [SerializeField] private RuntimeAnimatorController humanoidController;

        private readonly UtilityDecisionEngine _decisionEngine = new();
        private readonly UtilityDecisionPolicy _decisionPolicy = new();
        private readonly ReservationLedger _reservations = new();
        private readonly Dictionary<string, FacilityInteractionAnchor> _interactionAnchors = new(StringComparer.Ordinal);
        private readonly List<Material> _runtimeMaterials = new();
        private readonly float[] _deficits = new float[NeedCount];
        private readonly Dictionary<string, float> _waitingSeconds = new(StringComparer.Ordinal);

        private Transform _residentRoot;
        private Transform _residentVisual;
        private ResidentHumanoidPresentation _humanoidPresentation;
        private ResidentActionCandidate _currentAction;
        private ResidentDecisionResult _lastDecision;
        private ReservationLease _currentReservation;
        private ActionPhase _phase;
        private float _performRemaining;
        private float _visualClock;
        private long _decisionSequence;
        private float _generatorDamage;
        private float _haulBacklog;
        private float _repairPriority;
        private int _waterServings;
        private int _foodServings;
        private bool _simulationPaused;
        private GUIStyle _titleStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _selectedStyle;
        private Vector2 _traceScroll;

        /// <summary>最近一次纯 C# 决策结果，供灰盒观测与 PlayMode smoke 读取。</summary>
        public ResidentDecisionResult LastDecision => _lastDecision;

        /// <summary>当前正在移动或执行的行动 id；尚未取得行动时为空。</summary>
        public string CurrentActionId => _currentAction?.Id;

        /// <summary>展示器是否已经生成居民视觉根，用于区分初始化完成与只有组件存在。</summary>
        public bool HasGeneratedResident => _residentRoot != null;

        /// <summary>当前是否使用通过 Avatar 与状态契约验证的 Humanoid 表现，而不是程序假人。</summary>
        public bool HasHumanoidResident => _humanoidPresentation != null && _humanoidPresentation.IsReady;

        /// <summary>运行时生成并纳入行动导航的设施交互锚点数量。</summary>
        public int InteractionAnchorCount => _interactionAnchors.Count;

        /// <summary>已经提交给决策内核的快照数量。</summary>
        public long DecisionCount => _decisionSequence;

        private enum ActionPhase
        {
            Choosing,
            Moving,
            Performing,
            Waiting,
        }

        private void Awake()
        {
            ResetSimulationState();
            BuildGrayboxWorld();
        }

        private void Start()
        {
            ChooseNextAction();
        }

        private void Update()
        {
            float realDelta = Time.unscaledDeltaTime;
            _visualClock += realDelta;
            AnimateResident(realDelta);
            if (_simulationPaused) return;

            float simulationDelta = realDelta * simulationSpeed;
            AdvanceWorldState(simulationDelta);

            switch (_phase)
            {
                case ActionPhase.Choosing:
                    ChooseNextAction();
                    break;
                case ActionPhase.Moving:
                    TickMovement(simulationDelta);
                    break;
                case ActionPhase.Performing:
                    TickPerforming(simulationDelta);
                    break;
                case ActionPhase.Waiting:
                    _phase = ActionPhase.Choosing;
                    break;
            }
        }

        private void OnDestroy()
        {
            _currentReservation?.Dispose();
            for (int i = 0; i < _runtimeMaterials.Count; i++)
            {
                Material material = _runtimeMaterials[i];
                if (material != null) Destroy(material);
            }
        }

        private void ResetSimulationState()
        {
            _currentReservation?.Dispose();
            _currentReservation = null;
            _currentAction = null;
            _lastDecision = null;
            _decisionSequence = 0;
            _deficits[(int)ResidentNeed.Thirst] = 0.34f;
            _deficits[(int)ResidentNeed.Hunger] = 0.43f;
            _deficits[(int)ResidentNeed.Fatigue] = 0.28f;
            _deficits[(int)ResidentNeed.Health] = 0.06f;
            _generatorDamage = 0.28f;
            _haulBacklog = 0.42f;
            _repairPriority = 0.2f;
            _waterServings = 12;
            _foodServings = 12;
            _waitingSeconds.Clear();
            _waitingSeconds.Add("repair", 0f);
            _waitingSeconds.Add("haul", 0f);
            _phase = ActionPhase.Choosing;
        }

        private void BuildGrayboxWorld()
        {
            CreatePrimitive("Desert", PrimitiveType.Cube, new Vector3(0f, -0.55f, 0f),
                new Vector3(34f, 0.35f, 26f), new Color(0.34f, 0.25f, 0.17f));
            CreatePrimitive("VehicleDeck", PrimitiveType.Cube, new Vector3(0f, -0.15f, 0f),
                new Vector3(12f, 0.45f, 8f), new Color(0.2f, 0.23f, 0.25f));
            CreatePrimitive("FrontBulkhead", PrimitiveType.Cube, new Vector3(-5.75f, 0.45f, 0f),
                new Vector3(0.35f, 1.1f, 7.4f), new Color(0.28f, 0.31f, 0.32f));
            CreatePrimitive("RearRail", PrimitiveType.Cube, new Vector3(5.75f, 0.25f, 0f),
                new Vector3(0.18f, 0.55f, 7.4f), new Color(0.4f, 0.34f, 0.24f));
            CreatePrimitive("UpperRail", PrimitiveType.Cube, new Vector3(0f, 0.25f, 3.75f),
                new Vector3(11.3f, 0.55f, 0.18f), new Color(0.4f, 0.34f, 0.24f));
            CreatePrimitive("LowerRail", PrimitiveType.Cube, new Vector3(0f, 0.25f, -3.75f),
                new Vector3(11.3f, 0.55f, 0.18f), new Color(0.4f, 0.34f, 0.24f));

            AddStation("drink", "WaterTank", PrimitiveType.Cylinder, new Vector3(-4.15f, 0.55f, -2.45f),
                new Vector3(1f, 1.1f, 1f), new Color(0.12f, 0.55f, 0.83f));
            AddStation("eat", "MealTable", PrimitiveType.Cube, new Vector3(-1.35f, 0.38f, -2.55f),
                new Vector3(1.8f, 0.75f, 1.15f), new Color(0.72f, 0.46f, 0.18f));
            AddStation("rest", "Bed", PrimitiveType.Cube, new Vector3(2.35f, 0.25f, -2.45f),
                new Vector3(2.2f, 0.45f, 1.15f), new Color(0.28f, 0.62f, 0.5f));
            AddStation("repair", "Generator", PrimitiveType.Cylinder, new Vector3(3.75f, 0.62f, 1.65f),
                new Vector3(1.35f, 1.25f, 1.35f), new Color(0.78f, 0.3f, 0.19f));
            AddStation("haul", "Cargo", PrimitiveType.Cube, new Vector3(0.55f, 0.45f, 2.45f),
                new Vector3(2.1f, 0.9f, 1.25f), new Color(0.48f, 0.39f, 0.28f));
            AddStation("lookout", "Lookout", PrimitiveType.Cylinder, new Vector3(-4f, 0.28f, 2.45f),
                new Vector3(1.35f, 0.5f, 1.35f), new Color(0.48f, 0.36f, 0.72f));

            CreatePrimitive("DistantRuinA", PrimitiveType.Cube, new Vector3(-9f, 1.15f, 5.5f),
                new Vector3(2f, 3f, 1.4f), new Color(0.24f, 0.22f, 0.2f));
            CreatePrimitive("DistantRuinB", PrimitiveType.Cube, new Vector3(8.5f, 0.65f, 5.8f),
                new Vector3(3.2f, 1.7f, 1.4f), new Color(0.27f, 0.23f, 0.2f));
            CreateResident();
            SetupCameraAndLight();
        }

        private void AddStation(
            string actionId,
            string objectName,
            PrimitiveType primitive,
            Vector3 position,
            Vector3 scale,
            Color color)
        {
            GameObject station = CreatePrimitive(objectName, primitive, position, scale, color);
            Vector3 approach = position;
            approach.y = 0.12f;
            approach += position.x < 0f ? Vector3.right * 0.9f : Vector3.left * 0.9f;

            var standPointObject = new GameObject("InteractionStandPoint");
            standPointObject.transform.SetParent(station.transform, true);
            standPointObject.transform.position = approach;
            Vector3 facing = position - approach;
            facing.y = 0f;
            standPointObject.transform.rotation = facing.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(facing.normalized, Vector3.up)
                : Quaternion.identity;

            Transform handTarget = null;
            ResidentAnimationSemantic semantic = ResolveFacilitySemantic(actionId);
            if (semantic != ResidentAnimationSemantic.Idle && semantic != ResidentAnimationSemantic.Rest)
            {
                var handTargetObject = new GameObject("PrimaryHandTarget");
                handTargetObject.transform.SetParent(station.transform, true);
                handTargetObject.transform.position = position + Vector3.up * Math.Max(0.45f, scale.y * 0.42f);
                handTarget = handTargetObject.transform;
            }

            FacilityInteractionAnchor anchor = station.AddComponent<FacilityInteractionAnchor>();
            anchor.ConfigureRuntime(actionId, semantic, standPointObject.transform, handTarget);
            _interactionAnchors.Add(actionId, anchor);
        }

        private void CreateResident()
        {
            var root = new GameObject("Resident_Ada");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = new Vector3(0f, 0.12f, 0f);
            _residentRoot = root.transform;

            var humanoid = root.AddComponent<ResidentHumanoidPresentation>();
            if (humanoid.TryInitialize(humanoidPrefab, humanoidController))
            {
                _humanoidPresentation = humanoid;
                _residentVisual = humanoid.VisualRoot;
                return;
            }

            Destroy(humanoid);

            var visual = new GameObject("ProceduralPlaceholderVisual");
            visual.transform.SetParent(root.transform, false);
            _residentVisual = visual.transform;

            CreatePrimitive("Body", PrimitiveType.Capsule, new Vector3(0f, 0.85f, 0f),
                new Vector3(0.62f, 0.72f, 0.5f), new Color(0.18f, 0.63f, 0.7f), visual.transform, true);
            CreatePrimitive("Head", PrimitiveType.Sphere, new Vector3(0f, 1.72f, 0f),
                new Vector3(0.46f, 0.46f, 0.46f), new Color(0.86f, 0.67f, 0.52f), visual.transform, true);
            CreatePrimitive("Backpack", PrimitiveType.Cube, new Vector3(0f, 0.95f, -0.34f),
                new Vector3(0.48f, 0.62f, 0.25f), new Color(0.64f, 0.38f, 0.18f), visual.transform, true);
            CreatePrimitive("LeftArm", PrimitiveType.Capsule, new Vector3(-0.38f, 0.93f, 0f),
                new Vector3(0.19f, 0.48f, 0.19f), new Color(0.13f, 0.48f, 0.55f), visual.transform, true);
            CreatePrimitive("RightArm", PrimitiveType.Capsule, new Vector3(0.38f, 0.93f, 0f),
                new Vector3(0.19f, 0.48f, 0.19f), new Color(0.13f, 0.48f, 0.55f), visual.transform, true);
        }

        private static ResidentAnimationSemantic ResolveFacilitySemantic(string actionId)
        {
            return actionId switch
            {
                "repair" => ResidentAnimationSemantic.Work,
                "rest" => ResidentAnimationSemantic.Rest,
                "drink" or "eat" or "haul" => ResidentAnimationSemantic.Pickup,
                _ => ResidentAnimationSemantic.Idle,
            };
        }

        private GameObject CreatePrimitive(
            string objectName,
            PrimitiveType primitive,
            Vector3 position,
            Vector3 scale,
            Color color,
            Transform parent = null,
            bool localSpace = false)
        {
            GameObject instance = GameObject.CreatePrimitive(primitive);
            instance.name = objectName;
            instance.transform.SetParent(parent ?? transform, false);
            if (localSpace) instance.transform.localPosition = position;
            else instance.transform.position = position;
            instance.transform.localScale = scale;
            if (instance.TryGetComponent(out Collider collider)) collider.enabled = false;

            Material material = CreateMaterial(color);
            instance.GetComponent<Renderer>().sharedMaterial = material;
            return instance;
        }

        private Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { color = color };
            _runtimeMaterials.Add(material);
            return material;
        }

        private void SetupCameraAndLight()
        {
            Camera cameraComponent = FindMainCameraInOwnScene();
            GameObject cameraObject;
            if (cameraComponent == null)
            {
                cameraObject = new GameObject("Main Camera");
                cameraObject.transform.SetParent(transform, false);
                cameraObject.tag = "MainCamera";
                cameraComponent = cameraObject.AddComponent<Camera>();
            }
            else
            {
                cameraObject = cameraComponent.gameObject;
            }
            cameraComponent.orthographic = true;
            cameraComponent.orthographicSize = 7.2f;
            cameraComponent.nearClipPlane = 0.1f;
            cameraComponent.farClipPlane = 80f;
            cameraComponent.backgroundColor = new Color(0.12f, 0.1f, 0.09f);
            cameraObject.transform.position = new Vector3(10.5f, 12.5f, -11.5f);
            cameraObject.transform.LookAt(new Vector3(0f, 0.25f, 0f));

            var lightObject = new GameObject("Key Light");
            lightObject.transform.SetParent(transform, false);
            Light lightComponent = lightObject.AddComponent<Light>();
            lightComponent.type = LightType.Directional;
            lightComponent.intensity = 1.35f;
            lightComponent.color = new Color(1f, 0.82f, 0.66f);
            lightObject.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

            var fillObject = new GameObject("Fill Light");
            fillObject.transform.SetParent(transform, false);
            Light fill = fillObject.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.45f;
            fill.color = new Color(0.48f, 0.65f, 1f);
            fillObject.transform.rotation = Quaternion.Euler(55f, 145f, 0f);
        }

        private Camera FindMainCameraInOwnScene()
        {
            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera candidate = cameras[i];
                if (candidate.gameObject.scene == gameObject.scene && candidate.CompareTag("MainCamera"))
                    return candidate;
            }
            return null;
        }

        private void AdvanceWorldState(float deltaTime)
        {
            _deficits[(int)ResidentNeed.Thirst] = Clamp01(_deficits[(int)ResidentNeed.Thirst] + 0.0022f * deltaTime);
            _deficits[(int)ResidentNeed.Hunger] = Clamp01(_deficits[(int)ResidentNeed.Hunger] + 0.00135f * deltaTime);
            _deficits[(int)ResidentNeed.Fatigue] = Clamp01(_deficits[(int)ResidentNeed.Fatigue] + 0.00105f * deltaTime);

            bool physicallyCritical = _deficits[(int)ResidentNeed.Thirst] > 0.96f
                                      || _deficits[(int)ResidentNeed.Hunger] > 0.97f
                                      || _deficits[(int)ResidentNeed.Fatigue] > 0.98f;
            float healthDelta = physicallyCritical ? 0.0012f : -0.00018f;
            _deficits[(int)ResidentNeed.Health] = Clamp01(
                _deficits[(int)ResidentNeed.Health] + healthDelta * deltaTime);

            _generatorDamage = Clamp01(_generatorDamage + 0.0009f * deltaTime);
            _haulBacklog = Clamp01(_haulBacklog + 0.0007f * deltaTime);
            if (_generatorDamage > 0.05f) _waitingSeconds["repair"] += deltaTime;
            if (_haulBacklog > 0.05f) _waitingSeconds["haul"] += deltaTime;
        }

        private void ChooseNextAction()
        {
            List<ResidentActionCandidate> candidates = BuildCandidates();
            var context = new ResidentDecisionContext(
                worldSeed,
                ResidentId,
                _decisionSequence++,
                BuildNeedSnapshot(),
                candidates);
            _lastDecision = _decisionEngine.Decide(context, _decisionPolicy);

            if (!_lastDecision.HasSelection)
            {
                _currentAction = null;
                _phase = ActionPhase.Waiting;
                return;
            }

            _currentAction = _lastDecision.Selected;
            if (!_reservations.TryAcquire(ResidentId, _currentAction.ReservationKeys, out _currentReservation))
            {
                _currentAction = null;
                _phase = ActionPhase.Choosing;
                return;
            }

            _performRemaining = Math.Max(0.1f, _currentAction.DurationSeconds);
            _phase = ActionPhase.Moving;
        }

        private List<ResidentActionCandidate> BuildCandidates()
        {
            float DistanceCost(string id)
                => Vector3.Distance(_residentRoot.position, _interactionAnchors[id].StandPoint.position) * 0.018f;

            var drink = new ResidentActionCandidate("drink", "drink", "饮水")
            {
                DurationSeconds = 3f,
                BaseUtility = 0.025f,
                TravelCost = DistanceCost("drink"),
                DurationCost = 0.018f,
                IsAvailable = _waterServings > 0,
                BlockReason = _waterServings > 0 ? string.Empty : "储水已经耗尽",
                NeedEffects = new[] { new NeedEffect(ResidentNeed.Thirst, 0.66f) },
                ReservationKeys = new[] { "station:water", "resource:water-serving" },
            };
            var eat = new ResidentActionCandidate("eat", "eat", "进食")
            {
                DurationSeconds = 5f,
                BaseUtility = 0.02f,
                TravelCost = DistanceCost("eat"),
                DurationCost = 0.025f,
                IsAvailable = _foodServings > 0,
                BlockReason = _foodServings > 0 ? string.Empty : "食物已经耗尽",
                NeedEffects = new[]
                {
                    new NeedEffect(ResidentNeed.Hunger, 0.58f),
                    new NeedEffect(ResidentNeed.Thirst, 0.08f),
                    new NeedEffect(ResidentNeed.Fatigue, 0.045f),
                },
                ReservationKeys = new[] { "station:meal", "resource:food-serving" },
            };
            var rest = new ResidentActionCandidate("rest", "rest", "睡眠休息")
            {
                DurationSeconds = 8f,
                BaseUtility = 0.018f,
                TravelCost = DistanceCost("rest"),
                DurationCost = 0.035f,
                NeedEffects = new[] { new NeedEffect(ResidentNeed.Fatigue, 0.62f) },
                ReservationKeys = new[] { "station:bed" },
            };
            var repair = new ResidentActionCandidate("repair", "repair", "维修动力核心")
            {
                DurationSeconds = 6f,
                BaseUtility = 0.035f,
                WorkUrgency = _generatorDamage * 0.72f,
                PlayerPriority = _repairPriority,
                DependencyValue = _generatorDamage > 0.7f ? 0.24f : 0.06f,
                SkillFit = 0.11f,
                WaitingAge = Math.Min(0.24f, _waitingSeconds["repair"] * 0.0018f),
                TravelCost = DistanceCost("repair"),
                DurationCost = 0.04f,
                EmergencyPriority = _generatorDamage,
                IsAvailable = _generatorDamage > 0.02f,
                BlockReason = "动力核心无需维修",
                ReservationKeys = new[] { "station:generator", "tool:wrench" },
            };
            var haul = new ResidentActionCandidate("haul", "haul", "整理货物")
            {
                DurationSeconds = 4.5f,
                BaseUtility = 0.03f,
                WorkUrgency = _haulBacklog * 0.5f,
                PlayerPriority = 0.1f,
                SkillFit = 0.055f,
                WaitingAge = Math.Min(0.22f, _waitingSeconds["haul"] * 0.0016f),
                TravelCost = DistanceCost("haul"),
                DurationCost = 0.03f,
                IsAvailable = _haulBacklog > 0.03f,
                BlockReason = "没有待整理货物",
                ReservationKeys = new[] { "station:cargo", "item:cargo-batch" },
            };
            var lookout = new ResidentActionCandidate("lookout", "leisure", "眺望荒野")
            {
                DurationSeconds = 5f,
                BaseUtility = 0.14f,
                PersonalAffinity = 0.085f,
                TravelCost = DistanceCost("lookout"),
                DurationCost = 0.025f,
                SwitchCost = _generatorDamage > 0.65f ? 0.16f : 0f,
                ReservationKeys = new[] { "station:lookout" },
            };

            return new List<ResidentActionCandidate> { drink, eat, rest, repair, haul, lookout };
        }

        private ResidentNeedState[] BuildNeedSnapshot()
        {
            return new[]
            {
                new ResidentNeedState(ResidentNeed.Thirst, _deficits[(int)ResidentNeed.Thirst], 0.0022f, 1.2f),
                new ResidentNeedState(ResidentNeed.Hunger, _deficits[(int)ResidentNeed.Hunger], 0.00135f, 1f),
                new ResidentNeedState(ResidentNeed.Fatigue, _deficits[(int)ResidentNeed.Fatigue], 0.00105f, 0.92f),
                new ResidentNeedState(ResidentNeed.Health, _deficits[(int)ResidentNeed.Health], 0f, 1.4f),
            };
        }

        private void TickMovement(float deltaTime)
        {
            FacilityInteractionAnchor anchor = _interactionAnchors[_currentAction.Id];
            Vector3 target = anchor.StandPoint.position;
            Vector3 direction = target - _residentRoot.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0025f)
            {
                _residentRoot.position = target;
                _residentRoot.rotation = anchor.StandPoint.rotation;
                _phase = ActionPhase.Performing;
                return;
            }

            if (direction.sqrMagnitude > 0.0001f)
            {
                Quaternion facing = Quaternion.LookRotation(direction.normalized, Vector3.up);
                _residentRoot.rotation = Quaternion.Slerp(_residentRoot.rotation, facing, deltaTime * 5f);
            }
            _residentRoot.position = Vector3.MoveTowards(
                _residentRoot.position,
                target,
                residentMoveSpeed * deltaTime);
        }

        private void TickPerforming(float deltaTime)
        {
            _performRemaining -= deltaTime;
            if (_performRemaining > 0f) return;

            ApplyCurrentActionResult();
            _currentReservation?.Dispose();
            _currentReservation = null;
            _currentAction = null;
            _phase = ActionPhase.Choosing;
        }

        private void ApplyCurrentActionResult()
        {
            NeedEffect[] effects = _currentAction.NeedEffects ?? Array.Empty<NeedEffect>();
            for (int i = 0; i < effects.Length; i++)
            {
                int index = (int)effects[i].Need;
                _deficits[index] = Clamp01(_deficits[index] - effects[i].Restore);
            }

            switch (_currentAction.Id)
            {
                case "drink":
                    _waterServings = Math.Max(0, _waterServings - 1);
                    break;
                case "eat":
                    _foodServings = Math.Max(0, _foodServings - 1);
                    break;
                case "repair":
                    _generatorDamage = Clamp01(_generatorDamage - 0.66f);
                    _waitingSeconds["repair"] = 0f;
                    break;
                case "haul":
                    _haulBacklog = Clamp01(_haulBacklog - 0.58f);
                    _waitingSeconds["haul"] = 0f;
                    break;
            }
        }

        private void AnimateResident(float deltaTime)
        {
            if (_residentVisual == null) return;
            if (HasHumanoidResident)
            {
                _humanoidPresentation.SetPlaybackSpeed(_simulationPaused ? 0f : simulationSpeed);
                _humanoidPresentation.SetSemantic(ResolveCurrentAnimationSemantic());
                return;
            }

            bool moving = _phase == ActionPhase.Moving && !_simulationPaused;
            bool performing = _phase == ActionPhase.Performing && !_simulationPaused;
            float frequency = moving ? 11f : performing ? 5f : 2f;
            float amplitude = moving ? 0.065f : performing ? 0.035f : 0.012f;
            float bob = Mathf.Sin(_visualClock * frequency) * amplitude;
            _residentVisual.localPosition = new Vector3(0f, bob, 0f);
            float lean = moving ? Mathf.Sin(_visualClock * frequency) * 3.5f : 0f;
            _residentVisual.localRotation = Quaternion.Slerp(
                _residentVisual.localRotation,
                Quaternion.Euler(lean, 0f, -lean * 0.5f),
                deltaTime * 8f);
        }

        private ResidentAnimationSemantic ResolveCurrentAnimationSemantic()
        {
            if (_phase == ActionPhase.Moving) return ResidentAnimationSemantic.Move;
            if (_phase == ActionPhase.Performing && _currentAction != null &&
                _interactionAnchors.TryGetValue(_currentAction.Id, out FacilityInteractionAnchor anchor))
                return anchor.AnimationSemantic;
            return ResidentAnimationSemantic.Idle;
        }

        private void OnGUI()
        {
            EnsureGuiStyles();
            float width = Math.Min(560f, Screen.width * 0.45f);
            GUILayout.BeginArea(new Rect(16f, 16f, width, Screen.height - 32f), GUI.skin.box);
            GUILayout.Label("《游牧工坊》居民 Utility AI 灰盒", _titleStyle);
            GUILayout.Label(
                HasHumanoidResident
                    ? "实时 Humanoid 已接入五类共享动作；模拟仍拥有移动、占用与行动结算。"
                    : "未配置或未通过验证时自动回退程序假人，便于隔离资产管线故障。",
                _smallStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_simulationPaused ? "继续模拟" : "暂停模拟"))
                _simulationPaused = !_simulationPaused;
            if (GUILayout.Button("制造严重故障"))
            {
                _generatorDamage = 0.96f;
                InterruptForPolicyChange();
            }
            if (GUILayout.Button("重置"))
            {
                ResetSimulationState();
                if (_residentRoot != null) _residentRoot.position = new Vector3(0f, 0.12f, 0f);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"维修优先级：{_repairPriority:F2}", GUILayout.Width(150f));
            if (GUILayout.Button("降低"))
            {
                _repairPriority = Clamp01(_repairPriority - 0.15f);
                InterruptForPolicyChange();
            }
            if (GUILayout.Button("提高"))
            {
                _repairPriority = Clamp01(_repairPriority + 0.15f);
                InterruptForPolicyChange();
            }
            GUILayout.Label($"模拟倍率 ×{simulationSpeed:F1}", GUILayout.Width(110f));
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("居民状态", _selectedStyle);
            DrawNeed("口渴", ResidentNeed.Thirst);
            DrawNeed("饥饿", ResidentNeed.Hunger);
            DrawNeed("疲劳", ResidentNeed.Fatigue);
            DrawNeed("健康压力", ResidentNeed.Health);
            GUILayout.Label($"动力损伤 {_generatorDamage:P0}　货物积压 {_haulBacklog:P0}　水 {_waterServings}　食物 {_foodServings}");
            string actionText = _currentAction == null ? "等待下一次决策" : $"{PhaseName(_phase)}：{_currentAction.DisplayName}";
            GUILayout.Label($"当前行动：{actionText}", _selectedStyle);

            GUILayout.Space(8f);
            GUILayout.Label("最近一次候选解释", _selectedStyle);
            _traceScroll = GUILayout.BeginScrollView(_traceScroll, GUILayout.ExpandHeight(true));
            if (_lastDecision == null)
            {
                GUILayout.Label("尚未决策。", _smallStyle);
            }
            else
            {
                GUILayout.Label($"决策序号 {_decisionSequence - 1}　随机值 {_lastDecision.RandomRoll:F4}", _smallStyle);
                for (int i = 0; i < _lastDecision.Traces.Count; i++)
                    DrawTrace(_lastDecision.Traces[i]);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void InterruptForPolicyChange()
        {
            if (_phase == ActionPhase.Performing && _currentAction != null) return;
            _currentReservation?.Dispose();
            _currentReservation = null;
            _currentAction = null;
            _phase = ActionPhase.Choosing;
        }

        private void DrawNeed(string label, ResidentNeed need)
        {
            float value = _deficits[(int)need];
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(74f));
            Rect rect = GUILayoutUtility.GetRect(120f, 16f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none);
            Color previous = GUI.color;
            GUI.color = value >= _decisionPolicy.CriticalDeficit
                ? new Color(0.95f, 0.25f, 0.18f)
                : new Color(0.91f, 0.65f, 0.22f);
            GUI.Box(new Rect(rect.x + 2f, rect.y + 2f, (rect.width - 4f) * value, rect.height - 4f), GUIContent.none);
            GUI.color = previous;
            GUILayout.Label(value.ToString("P0"), GUILayout.Width(48f));
            GUILayout.EndHorizontal();
        }

        private void DrawTrace(CandidateDecisionTrace trace)
        {
            bool selected = trace.State == CandidateDecisionState.Selected;
            string marker = selected ? "▶" : trace.IsEmergency ? "!" : "·";
            GUIStyle style = selected ? _selectedStyle : _smallStyle;
            GUILayout.Label(
                $"{marker} {trace.Candidate.DisplayName}　U {trace.Score.Total:F3}　P {trace.Probability:P0}　{StateName(trace.State)}",
                style);
            GUILayout.Label(
                $"　基础 {trace.Score.BaseBenefit:F2} / 需求 {trace.Score.NeedBenefit:F2} / 工作 {trace.Score.WorkBenefit:F2} / 人格 {trace.Score.PersonalBenefit:F2} / 等待 {trace.Score.PersistenceBenefit:F2} / 成本 {trace.Score.ExecutionCost:F2}　{trace.Reason}",
                _smallStyle);
        }

        private void EnsureGuiStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
            };
            _smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
            };
            _selectedStyle = new GUIStyle(_smallStyle)
            {
                fontStyle = FontStyle.Bold,
            };
            _selectedStyle.normal.textColor = new Color(0.95f, 0.78f, 0.3f);
        }

        private static string PhaseName(ActionPhase phase)
        {
            return phase switch
            {
                ActionPhase.Moving => "前往",
                ActionPhase.Performing => "执行",
                ActionPhase.Choosing => "决策",
                _ => "等待",
            };
        }

        private static string StateName(CandidateDecisionState state)
        {
            return state switch
            {
                CandidateDecisionState.Ineligible => "不可执行",
                CandidateDecisionState.SupersededByIntent => "同意图被替代",
                CandidateDecisionState.OutsideEmergencyPool => "被紧急层排除",
                CandidateDecisionState.OutsideShortlist => "短名单外",
                CandidateDecisionState.Shortlisted => "未抽中",
                CandidateDecisionState.Selected => "已选择",
                _ => state.ToString(),
            };
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            return value > 1f ? 1f : value;
        }
    }
}
