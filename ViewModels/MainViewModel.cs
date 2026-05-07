using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using EFieldSimulation.Helpers;
using EFieldSimulation.Models;
using EFieldSimulation.Rendering;
using EFieldSimulation.Services;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Legends;
using System.Runtime.CompilerServices;
using System.IO;

namespace EFieldSimulation.ViewModels;

public sealed class MainViewModel : BaseViewModel
{
    private readonly FieldSuperposition _superposition = new();

    // ── Scene entries ────────────────────────────────────────
    public ObservableCollection<SceneEntry> SceneEntries { get; } = new();

    private SceneEntry? _selectedEntry;
    public SceneEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                OnSelectionChanged();
                OnPropertyChanged(nameof(IsTransformEnabled));
                OnPropertyChanged(nameof(CanAttachMesh));
                OnPropertyChanged(nameof(CanAttachField));
                OnPropertyChanged(nameof(SelectedHasShape));
            }
        }
    }

    private bool _isPopulated;
    public bool IsPopulated
    {
        get => _isPopulated;
        set
        {
            if (SetProperty(ref _isPopulated, value))
                OnPropertyChanged(nameof(IsTransformEnabled));
        }
    }

    /// Transform enabled: always before populate; after populate only for Movable entries
    public bool IsTransformEnabled =>
        _selectedEntry != null &&
        (!_isPopulated || _selectedEntry.Kind == SceneEntryKind.Movable);

    /// Can attach a mesh/field to the selected entry
    public bool CanAttachMesh => _selectedEntry != null;
    public bool CanAttachField => _selectedEntry != null;
    public bool SelectedHasShape => _selectedEntry?.ShapeParams != null;

    // ── 3D scene ─────────────────────────────────────────────
    private Model3DGroup _sceneGroup = new();
    public Model3DGroup SceneGroup
    {
        get => _sceneGroup;
        set => SetProperty(ref _sceneGroup, value);
    }

    // Caches converted WPF mesh geometry so scene rebuilds do not repeatedly reprocess the same mesh data.
    private readonly Dictionary<MeshData, MeshGeometry3D> _meshGeometryCache = new();

    private WriteableBitmap? _fieldSliceImage;
    public WriteableBitmap? FieldSliceImage
    {
        get => _fieldSliceImage;
        set => SetProperty(ref _fieldSliceImage, value);
    }
    // ── Probe backing fields ─────────────────────────────────
    private ProbeDefinition? _selectedProbe;
    private bool _showProbes = true;
    private string _probeResultText = "No probes evaluated.";
    private bool _probeEvalInFlight;
    // ── Probes ───────────────────────────────────────────────
    public ObservableCollection<ProbeDefinition> Probes { get; } = new();

    public ProbeDefinition? SelectedProbe
    {
        get => _selectedProbe;
        set
        {
            if (SetProperty(ref _selectedProbe, value))
            {
                OnPropertyChanged(nameof(IsProbeSelected));
                OnPropertyChanged(nameof(IsLineProbeSelected));
                OnPropertyChanged(nameof(IsPointProbeSelected));
                UpdateProbeResultDisplay();
            }
        }
    }

    public bool IsProbeSelected => _selectedProbe != null;
    public bool IsPointProbeSelected =>
        _selectedProbe is { Type: ProbeType.Point };
    public bool IsLineProbeSelected =>
        _selectedProbe is { Type: ProbeType.LineSegment };

    public bool ShowProbes
    {
        get => _showProbes;
        set { if (SetProperty(ref _showProbes, value)) RebuildScene(); }
    }

    public string ProbeResultText
    {
        get => _probeResultText;
        set => SetProperty(ref _probeResultText, value);
    }

    /// <summary>Flat list of samples from the selected line probe, for DataGrid binding.</summary>
    public ObservableCollection<ProbePointSample> ProbeLineSamples { get; } = new();

    // ── Tetrahedralization parameters ────────────────────────

    private double _tetMaxVolumeCm3 = 0.01;
    public double TetMaxVolumeCm3
    {
        get => _tetMaxVolumeCm3;
        set => SetProperty(ref _tetMaxVolumeCm3, Math.Max(1e-8, value));
    }

    private double _tetChargeDensity = 1e15;
    public double TetChargeDensity
    {
        get => _tetChargeDensity;
        set => SetProperty(ref _tetChargeDensity, Math.Clamp(value, 0, 1e17));
    }

    private int _tetFieldGridDensity = 96;
    public int TetFieldGridDensity
    {
        get => _tetFieldGridDensity;
        set => SetProperty(ref _tetFieldGridDensity, Math.Clamp(value, 8, 256));
    }

    private bool _isTetInnerPositive = true;
    public bool IsTetInnerPositive
    {
        get => _isTetInnerPositive;
        set { if (SetProperty(ref _isTetInnerPositive, value)) OnPropertyChanged(nameof(IsTetInnerNegative)); }
    }
    public bool IsTetInnerNegative
    {
        get => !_isTetInnerPositive;
        set => IsTetInnerPositive = !value;
    }
    private float _tetSofteningAlpha = 0.5f;
    public float TetSofteningAlpha
    {
        get => _tetSofteningAlpha;
        set => SetProperty(ref _tetSofteningAlpha, Math.Clamp(value, 0f, 2f));
    }
    private bool _balanceChargeVolumes;
    public bool BalanceChargeVolumes
    {
        get => _balanceChargeVolumes;
        set => SetProperty(ref _balanceChargeVolumes, value);
    }
    // ── Transform (retargetable) ─────────────────────────────
    public TransformViewModel MovableTransform { get; }
    private readonly TransformState _defaultTransformState = new();

    // ── Slice parameters ─────────────────────────────────────
    private int _sliceAxis = 2;
    public int SliceAxis
    {
        get => _sliceAxis;
        set { if (SetProperty(ref _sliceAxis, value)) { UpdateSliceAxisName(); UpdateFieldSlice(); } }
    }

    private float _slicePosition;
    public float SlicePosition
    {
        get => _slicePosition;
        set { if (SetProperty(ref _slicePosition, value)) UpdateFieldSlice(); }
    }

    private int _sliceResolution = 256;
    public int SliceResolution
    {
        get => _sliceResolution;
        set { if (SetProperty(ref _sliceResolution, value)) UpdateFieldSlice(); }
    }

    private string _sliceAxisName = "Z";
    public string SliceAxisName
    {
        get => _sliceAxisName;
        set => SetProperty(ref _sliceAxisName, value);
    }
    // - High-res bake
    private int _bakeResolution = 2048;
    public int BakeResolution
    {
        get => _bakeResolution;
        set => SetProperty(ref _bakeResolution, Math.Clamp(value, 256, 8192));
    }
    // ── Zero-field analysis ──────────────────────────────────

    private bool _showZeroContour;
    public bool ShowZeroContour
    {
        get => _showZeroContour;
        set { if (SetProperty(ref _showZeroContour, value)) UpdateFieldSlice(); }
    }

    private float _zeroFieldThresholdPercent = 2f;
    public float ZeroFieldThresholdPercent
    {
        get => _zeroFieldThresholdPercent;
        set
        {
            if (SetProperty(ref _zeroFieldThresholdPercent, Math.Clamp(value, 0.1f, 50f)))
                UpdateFieldSlice();
        }
    }

    private string _zeroPointStatusText = string.Empty;
    public string ZeroPointStatusText
    {
        get => _zeroPointStatusText;
        set => SetProperty(ref _zeroPointStatusText, value);
    }
    // ── Pathline backing fields ───────────────────────────────
    private bool _showPathlines = true;
    private bool _pathlineIsElectron = true;
    private double _pathlinePlaneCenterX = 0.0;
    private double _pathlinePlaneCenterY = 0.0;
    private double _pathlinePlaneCenterZ = 0.0;
    private double _pathlineNormalX = 0.0;
    private double _pathlineNormalY = 0.0;
    private double _pathlineNormalZ = 1.0;
    private double _pathlinePlaneWidth = 2.0;
    private double _pathlinePlaneHeight = 2.0;
    private int _pathlineGridDensity = 5;
    private double _pathlineInitialSpeed = 1e6;   // m/s
    private double _pathlineTimeStep = 1e-12; // s
    private int _pathlineMaxSteps = 400;
    private bool _tetInFlight;
    private bool _coulombComputeInProgress;

    // Rendered data (pairs of points = line segments)
    private System.Windows.Media.Media3D.Point3DCollection _pathlinePoints = new();
    public System.Windows.Media.Media3D.Point3DCollection PathlinePoints
    {
        get => _pathlinePoints;
        private set => SetProperty(ref _pathlinePoints, value);
    }
    private Point3DCollection _pathlinePlanePoints = new();
    public Point3DCollection PathlinePlanePoints
    {
        get => _pathlinePlanePoints;
        private set => SetProperty(ref _pathlinePlanePoints, value);
    }
    // ── Pathline properties ───────────────────────────────────
    public bool ShowPathlines
    {
        get => _showPathlines;
        set { if (SetProperty(ref _showPathlines, value)) RebuildScene(); }
    }

    public bool PathlineIsElectron
    {
        get => _pathlineIsElectron;
        set
        {
            if (SetProperty(ref _pathlineIsElectron, value))
            {
                OnPropertyChanged(nameof(PathlineIsProton));
                OnPropertyChanged(nameof(PathlineParticleColor));
            }
        }
    }
    public bool PathlineIsProton { get => !_pathlineIsElectron; set => PathlineIsElectron = !value; }

    public System.Windows.Media.Color PathlineParticleColor =>
        _pathlineIsElectron
            ? System.Windows.Media.Color.FromRgb(0, 220, 255)   // cyan = electron
            : System.Windows.Media.Color.FromRgb(255, 120, 40);  // orange = proton

    public double PathlinePlaneCenterX
    {
        get => _pathlinePlaneCenterX;
        set { if (SetProperty(ref _pathlinePlaneCenterX, value)) UpdatePathlinePlaneOutline(); }
    }
    public double PathlinePlaneCenterY
    {
        get => _pathlinePlaneCenterY;
        set { if (SetProperty(ref _pathlinePlaneCenterY, value)) UpdatePathlinePlaneOutline(); }
    }
    public double PathlinePlaneCenterZ
    {
        get => _pathlinePlaneCenterZ;
        set { if (SetProperty(ref _pathlinePlaneCenterZ, value)) UpdatePathlinePlaneOutline(); }
    }
    public double PathlineNormalX
    {
        get => _pathlineNormalX;
        set { if (SetProperty(ref _pathlineNormalX, value)) UpdatePathlinePlaneOutline(); }
    }
    public double PathlineNormalY
    {
        get => _pathlineNormalY;
        set { if (SetProperty(ref _pathlineNormalY, value)) UpdatePathlinePlaneOutline(); }
    }
    public double PathlineNormalZ
    {
        get => _pathlineNormalZ;
        set { if (SetProperty(ref _pathlineNormalZ, value)) UpdatePathlinePlaneOutline(); }
    }
    public double PathlinePlaneWidth
    {
        get => _pathlinePlaneWidth;
        set { if (SetProperty(ref _pathlinePlaneWidth, Math.Max(0.01, value))) UpdatePathlinePlaneOutline(); }
    }
    public double PathlinePlaneHeight
    {
        get => _pathlinePlaneHeight;
        set { if (SetProperty(ref _pathlinePlaneHeight, Math.Max(0.01, value))) UpdatePathlinePlaneOutline(); }
    }
    public int PathlineGridDensity
    {
        get => _pathlineGridDensity;
        set { if (SetProperty(ref _pathlineGridDensity, Math.Clamp(value, 1, 20))) UpdatePathlinePlaneOutline(); }
    }
    public double PathlineInitialSpeed
    {
        get => _pathlineInitialSpeed;
        set => SetProperty(ref _pathlineInitialSpeed, Math.Max(0, value));
    }
    public double PathlineTimeStep
    {
        get => _pathlineTimeStep;
        set => SetProperty(ref _pathlineTimeStep, Math.Max(1e-20, value));
    }
    public int PathlineMaxSteps
    {
        get => _pathlineMaxSteps;
        set => SetProperty(ref _pathlineMaxSteps, Math.Clamp(value, 10, 2000));
    }
    // ── Voltage surface settings ─────────────────────────────

    private int _voltageIntegrationSteps = 64;
    public int VoltageIntegrationSteps
    {
        get => _voltageIntegrationSteps;
        set => SetProperty(ref _voltageIntegrationSteps, Math.Clamp(value, 8, 512));
    }

    private string _voltageStatusText = "No voltage surfaces evaluated.";
    public string VoltageStatusText
    {
        get => _voltageStatusText;
        set => SetProperty(ref _voltageStatusText, value);
    }

    private bool _showVoltageSurfaces = true;
    public bool ShowVoltageSurfaces
    {
        get => _showVoltageSurfaces;
        set { if (SetProperty(ref _showVoltageSurfaces, value)) RebuildScene(); }
    }
    // ── Display ──────────────────────────────────────────────
    private double _emaxDisplayPercent = 100.0;
    public double EmaxDisplayPercent
    {
        get => _emaxDisplayPercent;
        set { if (SetProperty(ref _emaxDisplayPercent, Math.Clamp(value, 1, 100))) UpdateFieldSlice(); }
    }

    private int _arrowGridDensity = 8;
    public int ArrowGridDensity
    {
        get => _arrowGridDensity;
        set
        {
            if (SetProperty(ref _arrowGridDensity, Math.Clamp(value, 2, 24)))
            { OnPropertyChanged(nameof(ArrowTotalCount)); RebuildScene(); }
        }
    }
    public int ArrowTotalCount => _arrowGridDensity * _arrowGridDensity * _arrowGridDensity;

    private bool _showFieldArrows = true;
    public bool ShowFieldArrows
    {
        get => _showFieldArrows;
        set { if (SetProperty(ref _showFieldArrows, value)) RebuildScene(); }
    }

    private bool _showShape = true;
    public bool ShowShape
    {
        get => _showShape;
        set { if (SetProperty(ref _showShape, value)) RebuildScene(); }
    }

    private bool _showParticles = true;
    public bool ShowParticles
    {
        get => _showParticles;
        set { if (SetProperty(ref _showParticles, value)) RebuildScene(); }
    }

    private double _populateProgress;
    public double PopulateProgress
    {
        get => _populateProgress;
        set => SetProperty(ref _populateProgress, value);
    }

    private int _coulombGridDensity = 96;
    public int CoulombGridDensity
    {
        get => _coulombGridDensity;
        set => SetProperty(ref _coulombGridDensity, Math.Clamp(value, 8, 256));
    }

    // ── Probe graph backing fields ───────────────────────────
    private int _centerTabIndex;
    private PlotModel? _probePlotModel;
    private bool _showProbeGraphETotal = true;
    private bool _showProbeGraphEStatic = true;
    private bool _showProbeGraphEParticle = true;
    private bool _showProbeGraphVTotal = true;
    private bool _showProbeGraphVStatic = true;
    private bool _showProbeGraphVParticle = true;

    // ── Center panel tab ─────────────────────────────────────
    public int CenterTabIndex
    {
        get => _centerTabIndex;
        set => SetProperty(ref _centerTabIndex, value);
    }

    public bool HasProbePlotData => _probePlotModel != null;

    // ── Probe plot model (OxyPlot) ───────────────────────────
    public PlotModel? ProbePlotModel
    {
        get => _probePlotModel;
        set
        {
            if (SetProperty(ref _probePlotModel, value))
                OnPropertyChanged(nameof(HasProbePlotData));
        }
    }

    // ── Graph series toggles ─────────────────────────────────
    public bool ShowProbeGraphETotal
    {
        get => _showProbeGraphETotal;
        set { if (SetProperty(ref _showProbeGraphETotal, value)) UpdateProbePlot(); }
    }
    public bool ShowProbeGraphEStatic
    {
        get => _showProbeGraphEStatic;
        set { if (SetProperty(ref _showProbeGraphEStatic, value)) UpdateProbePlot(); }
    }
    public bool ShowProbeGraphEParticle
    {
        get => _showProbeGraphEParticle;
        set { if (SetProperty(ref _showProbeGraphEParticle, value)) UpdateProbePlot(); }
    }
    public bool ShowProbeGraphVTotal
    {
        get => _showProbeGraphVTotal;
        set { if (SetProperty(ref _showProbeGraphVTotal, value)) UpdateProbePlot(); }
    }
    public bool ShowProbeGraphVStatic
    {
        get => _showProbeGraphVStatic;
        set { if (SetProperty(ref _showProbeGraphVStatic, value)) UpdateProbePlot(); }
    }
    public bool ShowProbeGraphVParticle
    {
        get => _showProbeGraphVParticle;
        set { if (SetProperty(ref _showProbeGraphVParticle, value)) UpdateProbePlot(); }
    }

    // ── Charge volume / shape proxy ──────────────────────────
    private bool _isLoadingShape;
    private ArbitraryShapeParams? ActiveShapeParams =>
        _selectedEntry?.ShapeParams;

    private double _volumeChargeDensity = 1.0;
    public double VolumeChargeDensity
    {
        get => _volumeChargeDensity;
        set { if (SetProperty(ref _volumeChargeDensity, value))
        { Proxy(p => p.VolChargeDensity = value); OnPropertyChanged(nameof(ChargePerParticle)); } }
    }

    private bool _isChargePositive = true;
    public bool IsChargePositive
    {
        get => _isChargePositive;
        set { if (SetProperty(ref _isChargePositive, value))
        { Proxy(p => p.IsPositive = value);
          OnPropertyChanged(nameof(IsChargeNegative));
          OnPropertyChanged(nameof(ChargePerParticle)); } }
    }
    public bool IsChargeNegative { get => !_isChargePositive; set => IsChargePositive = !value; }

    private int _particleCount = 1000;
    public int ParticleCount
    {
        get => _particleCount;
        set { if (SetProperty(ref _particleCount, Math.Max(1, value)))
        { Proxy(p => p.VolParticleCount = value); OnPropertyChanged(nameof(ChargePerParticle)); } }
    }

    public double ChargePerParticle
    {
        get
        {
            double sign = _isChargePositive ? 1.0 : -1.0;
            return _particleCount > 0
                ? _volumeChargeDensity * EstimateShapeVolume() * sign / _particleCount
                : 0;
        }
    }

    private string _particleStatusText = "No particles placed.";
    public string ParticleStatusText
    {
        get => _particleStatusText;
        set => SetProperty(ref _particleStatusText, value);
    }

    // ── Shape definition (proxied to selected entry) ─────────
    private string _selectedShapeType = "Cylinder";
    public string SelectedShapeType
    {
        get => _selectedShapeType;
        set
        {
            if (SetProperty(ref _selectedShapeType, value))
            { Proxy(p => p.Type = value); OnPropertyChanged(nameof(ChargePerParticle)); RebuildScene(); }
        }
    }
    public static IReadOnlyList<string> ShapeTypes => ShapeLibrary.ShapeNames;

    private double _shapeRadius = 1.0;
    public double ShapeRadius
    {
        get => _shapeRadius;
        set { _shapeRadius = value; Proxy(p => { p.Radius = value; p.SphereRadius = value; });
              OnPropertyChanged(); OnPropertyChanged(nameof(ChargePerParticle)); RebuildScene(); }
    }

    private double _shapeHeight = 2.0;
    public double ShapeHeight
    {
        get => _shapeHeight;
        set { _shapeHeight = value; Proxy(p => { p.Height = value; p.ConeHeight = value; });
              OnPropertyChanged(); OnPropertyChanged(nameof(ChargePerParticle)); RebuildScene(); }
    }

    private double _shapeMajorRadius = 2.0;
    public double ShapeMajorRadius
    {
        get => _shapeMajorRadius;
        set { _shapeMajorRadius = value; Proxy(p => p.MajorRadius = value);
              OnPropertyChanged(); OnPropertyChanged(nameof(ChargePerParticle)); RebuildScene(); }
    }

    private double _shapeMinorRadius = 0.5;
    public double ShapeMinorRadius
    {
        get => _shapeMinorRadius;
        set { _shapeMinorRadius = value; Proxy(p => p.MinorRadius = value);
              OnPropertyChanged(); OnPropertyChanged(nameof(ChargePerParticle)); RebuildScene(); }
    }

    private double _shapeAngleStart;
    public double ShapeAngleStart
    {
        get => _shapeAngleStart;
        set { _shapeAngleStart = value; Proxy(p => p.AngleStartDeg = value);
              OnPropertyChanged(); RebuildScene(); }
    }

    private double _shapeAngleSpan = 360;
    public double ShapeAngleSpan
    {
        get => _shapeAngleSpan;
        set { _shapeAngleSpan = value; Proxy(p => p.AngleSpanDeg = value);
              OnPropertyChanged(); RebuildScene(); }
    }

    private double _shapeCenterX, _shapeCenterY, _shapeCenterZ;
    public double ShapeCenterX
    {
        get => _shapeCenterX;
        set { _shapeCenterX = value; Proxy(p => p.CenterX = value); OnPropertyChanged(); RebuildScene(); }
    }
    public double ShapeCenterY
    {
        get => _shapeCenterY;
        set { _shapeCenterY = value; Proxy(p => p.CenterY = value); OnPropertyChanged(); RebuildScene(); }
    }
    public double ShapeCenterZ
    {
        get => _shapeCenterZ;
        set { _shapeCenterZ = value; Proxy(p => p.CenterZ = value); OnPropertyChanged(); RebuildScene(); }
    }
    private double _shapeHelixTurns = 3.0;
    public double ShapeHelixTurns
    {
        get => _shapeHelixTurns;
        set
        {
            _shapeHelixTurns = value; Proxy(p => p.HelixTurns = value);
            OnPropertyChanged(); OnPropertyChanged(nameof(ChargePerParticle)); RebuildScene();
        }
    }
    private TetStatistics? _lastTetStats;
    public TetStatistics? LastTetStats
    {
        get => _lastTetStats;
        set => SetProperty(ref _lastTetStats, value);
    }

    private double _shapeHelixPitch = 1.0;
    public double ShapeHelixPitch
    {
        get => _shapeHelixPitch;
        set
        {
            _shapeHelixPitch = value; Proxy(p => p.HelixPitch = value);
            OnPropertyChanged(); OnPropertyChanged(nameof(ChargePerParticle)); RebuildScene();
        }
    }
    // ── Shape rotation (local, before entry transform) ───────
    private double _shapeRotX, _shapeRotY, _shapeRotZ;
    /*public double ShapeRotX
    {
        get => _shapeRotX;
        set { _shapeRotX = value; Proxy(p => p.RotationX = value); OnPropertyChanged(); RebuildScene(); }
    }
    public double ShapeRotY
    {
        get => _shapeRotY;
        set { _shapeRotY = value; Proxy(p => p.RotationY = value); OnPropertyChanged(); RebuildScene(); }
    }
    public double ShapeRotZ
    {
        get => _shapeRotZ;
        set { _shapeRotZ = value; Proxy(p => p.RotationZ = value); OnPropertyChanged(); RebuildScene(); }
    }*/

    /// Writes to the selected entry's ShapeParams if present and not loading
    private void Proxy(Action<ArbitraryShapeParams> action)
    {
        if (!_isLoadingShape && ActiveShapeParams != null) action(ActiveShapeParams);
    }

    // ── Status ───────────────────────────────────────────────
    private string _statusText = "Ready. Import meshes and field data to begin.";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    // ── Commands ─────────────────────────────────────────────
    public ICommand AddStaticEntryCommand { get; }
    public ICommand AddMovableEntryCommand { get; }
    public ICommand AddChargeVolumeCommand { get; }
    public ICommand AttachMeshCommand { get; }
    public ICommand AttachFieldCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ResetTransformCommand { get; }
    public ICommand PopulateShapeCommand { get; }
    public ICommand CalcParticleFieldCommand { get; }
    public ICommand ClearParticlesCommand { get; }
    public ICommand CalcPathlinesCommand { get; }
    public ICommand AddVoltageSurfaceCommand { get; }
    public ICommand CalcVoltageCommand { get; }
    public ICommand BakeHighResSliceCommand { get; }
    // Keep legacy names wired in toolbar
    public ICommand ImportStaticMeshCommand => AddStaticEntryCommand;
    public ICommand ImportMovableMeshCommand => AddMovableEntryCommand;
    public ICommand ImportStaticFieldCommand => AttachFieldCommand;
    public ICommand ImportMovableFieldCommand => AttachFieldCommand;
    public ICommand SaveProjectCommand { get; }
    public ICommand LoadProjectCommand { get; }
    public ICommand ExportFieldHdf5Command { get; }
    public ICommand AddPointProbeCommand { get; private set; } = null!;
    public ICommand AddLineProbeCommand { get; private set; } = null!;
    public ICommand EvaluateProbesCommand { get; private set; } = null!;
    public ICommand DeleteProbeCommand { get; private set; } = null!;

    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private bool _updatePending;
    private bool _coulombRecalcPending;
    private System.Windows.Threading.DispatcherTimer? _coulombTimer;
    private bool _coulombRecalcInFlight;

    public string GetProjectRoot([CallerFilePath] string sourceFilePath = "")
    {
        // Returns the directory containing this source file
        return Path.GetDirectoryName(sourceFilePath);
    }

    // ═════════════════════════════════════════════════════════
    public MainViewModel()
    {
        // Load file-driven shape library before any bindings/rebuilds touch it.
        try
        {
            ShapeLibrary.Load(System.IO.Path.Combine(
                GetProjectRoot(), "shapes.json"));
            _selectedShapeType = ShapeLibrary.ShapeNames.Contains(_selectedShapeType)
                ? _selectedShapeType
                : ShapeLibrary.ShapeNames.FirstOrDefault() ?? _selectedShapeType;
        }
        catch (Exception ex)
        {
            _statusText = $"shapes.json load failed: {ex.Message}";
        }

        MovableTransform = new TransformViewModel(_defaultTransformState);
        MovableTransform.TransformChanged += OnMovableTransformChanged;

        AddStaticEntryCommand = new RelayCommand(AddStaticEntry);
        AddMovableEntryCommand = new RelayCommand(AddMovableEntry);
        AddChargeVolumeCommand = new RelayCommand(OnAddChargeVolume);
        AttachMeshCommand = new RelayCommand(OnAttachMesh, () => _selectedEntry != null);
        AttachFieldCommand = new RelayCommand(OnAttachField, () => _selectedEntry != null);
        DeleteSelectedCommand = new RelayCommand(OnDeleteSelected, () => _selectedEntry != null);
        ResetTransformCommand = new RelayCommand(() => MovableTransform.Reset());
        PopulateShapeCommand = new RelayCommand(OnPopulateShape);
        CalcParticleFieldCommand = new RelayCommand(OnCalcParticleField);
        ClearParticlesCommand = new RelayCommand(OnClearParticles);
        CalcPathlinesCommand = new RelayCommand(OnCalcPathlines);
        AddVoltageSurfaceCommand = new RelayCommand(OnAddVoltageSurface);
        CalcVoltageCommand = new RelayCommand(OnCalcVoltage);
        BakeHighResSliceCommand = new RelayCommand(OnBakeHighResSlice);
        SaveProjectCommand = new RelayCommand(OnSaveProject);
        LoadProjectCommand = new RelayCommand(OnLoadProject);
        ExportFieldHdf5Command = new RelayCommand(OnExportFieldHdf5,
            () => _selectedEntry?.Field != null);
        AddPointProbeCommand = new RelayCommand(OnAddPointProbe);
        AddLineProbeCommand = new RelayCommand(OnAddLineProbe);
        EvaluateProbesCommand = new RelayCommand(OnEvaluateProbes);
        DeleteProbeCommand = new RelayCommand(OnDeleteProbe, () => _selectedProbe != null);

        _updateTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(50) };
        _updateTimer.Tick += (_, _) => { if (_updatePending) { _updatePending = false; PerformUpdate(); } };
        _updateTimer.Start();

        _coulombTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500) // longer debounce for heavy compute
        };

        _coulombTimer.Tick += (_, _) =>
        {
            if (_coulombRecalcPending && !_coulombRecalcInFlight)
            {
                _coulombRecalcPending = false;
                RecalcCoulombField();
            }
        };

        _coulombTimer.Start();
        UpdatePathlinePlaneOutline();

        RebuildScene();
    }

    // ── Selection ────────────────────────────────────────────

    /// <summary>
    /// Builds a wire rectangle + grid crosshair showing the pathline launch plane.
    /// Mirrors the solver's uAxis/vAxis construction exactly.
    /// </summary>
    private void UpdatePathlinePlaneOutline()
    {
        var col = new Point3DCollection();

        var n = new System.Numerics.Vector3(
            (float)_pathlineNormalX, (float)_pathlineNormalY, (float)_pathlineNormalZ);
        float nLen = n.Length();
        if (nLen < 1e-6f)
        {
            col.Freeze();
            PathlinePlanePoints = col;
            return;
        }
        var normal = n / nLen;
        var tmp = MathF.Abs(normal.Y) < 0.9f
            ? System.Numerics.Vector3.UnitY
            : System.Numerics.Vector3.UnitX;
        var uAxis = System.Numerics.Vector3.Normalize(
            System.Numerics.Vector3.Cross(normal, tmp));
        var vAxis = System.Numerics.Vector3.Cross(normal, uAxis);

        var center = new System.Numerics.Vector3(
            (float)_pathlinePlaneCenterX,
            (float)_pathlinePlaneCenterY,
            (float)_pathlinePlaneCenterZ);

        float hw = (float)_pathlinePlaneWidth * 0.5f;
        float hh = (float)_pathlinePlaneHeight * 0.5f;

        System.Numerics.Vector3 P(float fu, float fv)
            => center + uAxis * (fu * (float)_pathlinePlaneWidth)
                      + vAxis * (fv * (float)_pathlinePlaneHeight);

        static Point3D W(System.Numerics.Vector3 p) => new(p.X, p.Y, p.Z);

        // Outer rectangle
        var c00 = P(-0.5f, -0.5f);
        var c10 = P(0.5f, -0.5f);
        var c11 = P(0.5f, 0.5f);
        var c01 = P(-0.5f, 0.5f);
        void Seg(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
        { col.Add(W(a)); col.Add(W(b)); }
        Seg(c00, c10); Seg(c10, c11); Seg(c11, c01); Seg(c01, c00);

        // Interior grid lines to indicate density
        int g = Math.Max(1, _pathlineGridDensity);
        if (g > 1)
        {
            for (int i = 1; i < g; i++)
            {
                float f = i / (float)(g - 1) - 0.5f;
                Seg(P(f, -0.5f), P(f, 0.5f)); // vertical
                Seg(P(-0.5f, f), P(0.5f, f)); // horizontal
            }
        }

        // Short normal indicator from center
        float arrowLen = 0.3f * MathF.Min(hw, hh);
        if (arrowLen < 0.05f) arrowLen = 0.05f;
        Seg(center, center + normal * arrowLen);

        col.Freeze();
        PathlinePlanePoints = col;
    }

    private void OnSelectionChanged()
    {
        var entry = _selectedEntry;
        MovableTransform.SetTarget(entry?.Transform ?? _defaultTransformState);
        if (entry?.ShapeParams != null)
            LoadShapeFromEntry(entry);
        OnPropertyChanged(nameof(SelectedHasShape));
    }

    private void LoadShapeFromEntry(SceneEntry entry)
    {
        var p = entry.ShapeParams!;
        _isLoadingShape = true;
        try
        {
            _selectedShapeType = p.Type; OnPropertyChanged(nameof(SelectedShapeType));
            _shapeRadius = p.Radius; OnPropertyChanged(nameof(ShapeRadius));
            _shapeHeight = p.Height; OnPropertyChanged(nameof(ShapeHeight));
            _shapeMajorRadius = p.MajorRadius; OnPropertyChanged(nameof(ShapeMajorRadius));
            _shapeMinorRadius = p.MinorRadius; OnPropertyChanged(nameof(ShapeMinorRadius));
            _shapeAngleStart = p.AngleStartDeg; OnPropertyChanged(nameof(ShapeAngleStart));
            _shapeAngleSpan = p.AngleSpanDeg; OnPropertyChanged(nameof(ShapeAngleSpan));
            _shapeHelixTurns = p.HelixTurns; OnPropertyChanged(nameof(ShapeHelixTurns));
            _shapeHelixPitch = p.HelixPitch; OnPropertyChanged(nameof(ShapeHelixPitch));
            _volumeChargeDensity = p.VolChargeDensity; OnPropertyChanged(nameof(VolumeChargeDensity));
            _isChargePositive = p.IsPositive;
            OnPropertyChanged(nameof(IsChargePositive));
            OnPropertyChanged(nameof(IsChargeNegative));
            _particleCount = p.VolParticleCount; OnPropertyChanged(nameof(ParticleCount));
            OnPropertyChanged(nameof(ChargePerParticle));
        }
        finally { _isLoadingShape = false; }
    }

    // ── Entry creation ───────────────────────────────────────

    private SceneEntry CreateEntry(SceneEntryKind kind, string prefix)
    {
        var entry = new SceneEntry { Kind = kind, Name = $"{prefix} {SceneEntries.Count + 1}" };
        entry.PropertyChanged += OnEntryPropertyChanged;
        SceneEntries.Add(entry);
        SelectedEntry = entry;
        return entry;
    }
    private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneEntry.IsVisible))
        {
            RebuildScene();
            UpdateFieldSlice();
        }
    }
    private void AddStaticEntry()
    {
        var path = BrowseFile("3D Mesh Files|*.stl;*.obj;*.ply|All Files|*.*");
        if (path == null) return;
        try
        {
            var mesh = MeshImporter.Import(path);
            var entry = CreateEntry(SceneEntryKind.Static, "Static");
            entry.Name = $"Static {mesh.Name}";
            entry.Mesh = mesh;
            entry.NotifyContentChanged();
            StatusText = $"Static entry created with mesh: {mesh.Name}";
            RebuildScene();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    private void AddMovableEntry()
    {
        var path = BrowseFile("3D Mesh Files|*.stl;*.obj;*.ply|All Files|*.*");
        if (path == null) return;
        try
        {
            var mesh = MeshImporter.Import(path);
            var entry = CreateEntry(SceneEntryKind.Movable, "Movable");
            entry.Name = $"Movable: {mesh.Name}";
            entry.Mesh = mesh;
            entry.NotifyContentChanged();
            StatusText = $"Movable entry created with mesh: {mesh.Name}";
            RebuildScene();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    private void OnAddChargeVolume()
    {
        var entry = CreateEntry(SceneEntryKind.ChargeVolume, "Charge Volume");
        entry.ShapeParams = new ArbitraryShapeParams();
        entry.NotifyContentChanged();
        LoadShapeFromEntry(entry);
        StatusText = $"Added: {entry.Name}";
        RebuildScene();
    }
    private void OnAddVoltageSurface()
    {
        var entry = CreateEntry(SceneEntryKind.VoltageSurface, "Voltage Surface");
        entry.ShapeParams = new ArbitraryShapeParams();
        entry.NotifyContentChanged();
        LoadShapeFromEntry(entry);
        StatusText = $"Added: {entry.Name} (define shape, then Calculate Voltage).";
        RebuildScene();
    }

    private async void OnCalcVoltage()
    {
        var surfaces = SceneEntries
            .Where(e => e.Kind == SceneEntryKind.VoltageSurface
                     && e.ShapeParams != null && e.IsVisible)
            .ToList();

        if (surfaces.Count == 0)
        { StatusText = "No voltage surfaces defined."; return; }

        var fieldSources = GetFieldEntries();
        if (fieldSources.Count == 0)
        {
            StatusText = "No E-field sources. Import/compute a field first.";
            return;
        }

        StatusText = $"Integrating V=∫E·dl on {surfaces.Count} surface(s)… (V∞=0)";
        VoltageStatusText = "Calculating…";

        var progress = new Progress<double>(p => PopulateProgress = p * 100);

        try
        {
            var capturedSources = fieldSources.ToList();
            var capturedSurfs = surfaces.ToList();
            var results = await Task.Run(() =>
                VoltageSolver.ComputeAll(capturedSurfs, capturedSources,
                                         _voltageIntegrationSteps, progress));

            foreach (var r in results)
            {
                r.Entry.VoltageResult = r;
                r.Entry.NotifyContentChanged();
            }

            // Σ|Q| across populated charge volumes
            double totalChargeAbs = 0;
            foreach (var ce in SceneEntries)
            {
                if (ce.Kind != SceneEntryKind.ChargeVolume || ce.Particles == null) continue;
                totalChargeAbs += Math.Abs(ce.Particles.ChargePerParticle) * ce.Particles.Count;
            }

            double vSum = 0; long vN = 0;
            foreach (var r in results) { foreach (var v in r.Voltages) { vSum += v; vN++; } }
            double vAvgAll = vN > 0 ? vSum / vN : 0;
            double storedEnergy = 0.5 * totalChargeAbs * vAvgAll;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(
                $"Σ|Q| = {totalChargeAbs:E4} C   " +
                $"V̄ = {vAvgAll:E4} V   " +
                $"U = ½·Σ|Q|·V̄ = {storedEnergy:E4} J");
            foreach (var r in results)
            {
                sb.AppendLine(
                    $"{r.Entry.Name}: V∈[{r.MinVoltage:E3} … {r.MaxVoltage:E3}] V  " +
                    $"(avg {r.AverageVoltage:E3} V, n={r.Voltages.Length})");
                sb.AppendLine(
                    $"        |E|∈[{r.MinFieldMag:E3} … {r.MaxFieldMag:E3}] V/m  " +
                    $"(avg {r.AverageFieldMag:E3} V/m)");
            }
            VoltageStatusText = sb.ToString().TrimEnd();

            StatusText =
                $"Voltage: {results.Count} surface(s). " +
                $"Σ|Q|={totalChargeAbs:E3} C, V̄={vAvgAll:E3} V, U={storedEnergy:E3} J.";
            PopulateProgress = 100;
            RebuildScene();
        }
        catch (Exception ex)
        {
            StatusText = $"Voltage calc error: {ex.Message}";
            VoltageStatusText = "Calculation failed.";
        }
    }

    // ── Attach mesh/field to existing selected entry ─────────

    private async void OnAttachMesh()
    {
        if (_tetInFlight) { StatusText = "Tetrahedralization already running."; return; }
        if (_selectedEntry == null) return;

        _tetInFlight = true;

        if (_selectedEntry.Mesh == null)
        {
            var path = BrowseFile("3D Mesh Files|*.stl;*.obj;*.ply|All Files|*.*");
            if (path == null) { _tetInFlight = false; return; }
            try
            {
                var mesh = MeshImporter.Import(path);
                _selectedEntry.Mesh = mesh;
                _selectedEntry.Name = $"{_selectedEntry.Kind}: {mesh.Name}";
                _selectedEntry.NotifyContentChanged();
                RebuildScene();
            }
            catch (Exception ex) { StatusText = $"Import error: {ex.Message}"; _tetInFlight = false; return; }
        }

        var meshData = _selectedEntry.Mesh!;
        StatusText = $"Tetrahedralizing '{meshData.Name}' ({_tetChargeDensity:E1}/cm³, " +
                     $"max vol {_tetMaxVolumeCm3:E2} cm³, {_tetFieldGridDensity}³ grid)…";
        PopulateProgress = 0;

        var maxVol = _tetMaxVolumeCm3;
        var density = _tetChargeDensity;
        var fGrid = _tetFieldGridDensity;
        var innerPos = _isTetInnerPositive;
        var progress = new Progress<double>(p => PopulateProgress = p * 100);
        var entry = _selectedEntry;
        var softAlpha = _tetSofteningAlpha;

        try
        {
            var (field, stats) = await Task.Run(() =>
                MeshTetrahedralizer.Tetrahedralize(
                    meshData, density, maxVol, fGrid, innerPos, softAlpha, progress));

            entry.Field = field;
            entry.FieldAccessor = field.BuildStructuredAccessor();
            entry.TetStats = stats;
            entry.NotifyContentChanged();

            LastTetStats = stats;

            PopulateProgress = 100;
            StatusText = $"Done: {field.Description}";
            UpdateFieldSlice();
            RebuildScene();
        }
        catch (Exception ex)
        {
            StatusText = $"Tetrahedralization error: {ex.Message}";
        }
        finally { _tetInFlight = false; }
    }

    private void OnAttachField()
    {
        if (_selectedEntry == null) return;
        var path = BrowseFile("HDF5 Files|*.h5;*.hdf5|All Files|*.*");
        if (path == null) return;
        try
        {
            var field = Hdf5FieldLoader.Load(path);
            _selectedEntry.Field = field;
            _selectedEntry.FieldAccessor = field.BuildStructuredAccessor();
            _selectedEntry.NotifyContentChanged();
            StatusText = $"Attached E-field ({field.PointCount} pts) to {_selectedEntry.Name}";
            UpdateFieldSlice();
            RebuildScene();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    // ── Delete ───────────────────────────────────────────────

    private void OnDeleteSelected()
    {
        if (_selectedEntry == null) return;
        var name = _selectedEntry.Name;
        _selectedEntry.PropertyChanged -= OnEntryPropertyChanged;

        // Removes cached geometry for deleted meshes so unused render data does not stay in memory.
        if (_selectedEntry.Mesh != null)
            _meshGeometryCache.Remove(_selectedEntry.Mesh);

        SceneEntries.Remove(_selectedEntry);
        SelectedEntry = SceneEntries.FirstOrDefault();
        StatusText = $"Deleted: {name}";
        RebuildScene(); UpdateFieldSlice();
    }
    // ── Populate / Calc / Clear ──────────────────────────────

    private void OnPopulateShape()
    {
        var volumes = SceneEntries
            .Where(e => e.Kind == SceneEntryKind.ChargeVolume
                     && e.ShapeParams != null && e.IsVisible).ToList();

        if (volumes.Count == 0)
        {
            StatusText = "No charge volumes to populate.";
            return;
        }

        // ── Charge balancing: scale down the larger side ──
        if (_balanceChargeVolumes)
            BalanceChargeDensities(volumes);

        RemoveCoulombEntries();

        int totalParticles = 0;
        foreach (var entry in volumes)
        {
            var sp = entry.ShapeParams!;

            Console.WriteLine($"[DEBUG] Shape '{entry.Name}':");
            Console.WriteLine($"        VolChargeDensity = {sp.VolChargeDensity:E3}");
            Console.WriteLine($"        IsPositive       = {sp.IsPositive}");
            Console.WriteLine($"        VolParticleCount = {sp.VolParticleCount}");

            var cloud = ParticlePopulator.Populate(entry.ShapeParams!, entry.Transform);

            Console.WriteLine($"        ChargePerParticle = {cloud.ChargePerParticle:E3} C");
            Console.WriteLine($"        Expected sign     = {(sp.IsPositive ? "+" : "−")}");
            Console.WriteLine($"        Actual sign       = {(cloud.ChargePerParticle > 0 ? "+" : "−")}");
            Console.WriteLine();

            entry.Particles = cloud;
            entry.NotifyContentChanged();
            totalParticles += cloud.Count;
        }

        IsPopulated = true;
        ParticleStatusText = $"Placed {totalParticles:N0} particles across " +
                             $"{volumes.Count} volume(s). Scene locked.";
        StatusText = "Particles populated — only Movable entries can be transformed.";
        RebuildScene();

        // Invalidate all derived results
        foreach (var e in SceneEntries)
        {
            if (e.Kind == SceneEntryKind.VoltageSurface && e.VoltageResult != null)
            { e.VoltageResult = null; e.NotifyContentChanged(); }
        }

        // Force slice update to not show stale superposition
        UpdateFieldSlice();
    }

    /// <summary>
    /// Ensures |Σ positive charge| == |Σ negative charge| across all charge volumes
    /// by scaling down densities on whichever side has greater total magnitude.
    /// </summary>
    private void BalanceChargeDensities(List<SceneEntry> volumes)
    {
        double posTotal = 0, negTotal = 0;

        foreach (var entry in volumes)
        {
            var sp = entry.ShapeParams!;
            double vol = ShapeLibrary.EvaluateVolume(sp);
            double mag = Math.Abs(sp.VolChargeDensity) * vol;
            if (sp.IsPositive) posTotal += mag;
            else negTotal += mag;
        }

        if (posTotal < 1e-30 && negTotal < 1e-30) return;
        if (posTotal < 1e-30 || negTotal < 1e-30) return; // one side is zero; nothing to balance against

        double diff = posTotal - negTotal;
        if (Math.Abs(diff) < 1e-30 * Math.Max(posTotal, negTotal))
        {
            Console.WriteLine("  Volume charge balance: already balanced.");
            return;
        }

        // Scale down the larger side
        bool scalePositive = posTotal > negTotal;
        double alpha = scalePositive ? negTotal / posTotal : posTotal / negTotal;

        foreach (var entry in volumes)
        {
            var sp = entry.ShapeParams!;
            if (sp.IsPositive == scalePositive)
            {
                sp.VolChargeDensity *= alpha;
            }
        }

        string side = scalePositive ? "positive" : "negative";
        Console.WriteLine($"  Volume charge balance: scaled {side} densities by α={alpha:F10}");

        // Refresh UI if selected entry was affected
        if (_selectedEntry != null && volumes.Contains(_selectedEntry))
        {
            _volumeChargeDensity = _selectedEntry.ShapeParams!.VolChargeDensity;
            OnPropertyChanged(nameof(VolumeChargeDensity));
            OnPropertyChanged(nameof(ChargePerParticle));
        }
    }

    private async void RecalcCoulombField()
    {
        // add a second inner guard for redundancy
        if (_coulombComputeInProgress)
        {
            _coulombRecalcPending = true;  // re-queue
            return;
        }
        _coulombComputeInProgress = true;

        try
        {
            var sources = SceneEntries
                .Where(e => e.Particles != null && e.ShapeParams != null && e.IsVisible)
                .Select(e => (e, e.Particles!))
                .ToList();

            if (sources.Count == 0) return;

            var bounds = GetImportedFieldBounds();
            if (bounds == null) return;

            // This prevents the slice from showing stale data during compute
            RemoveCoulombEntries();
            UpdateFieldSlice();  // show tet-only field while computing

            var (gridMin, gridMax) = bounds.Value;
            int totalQ = sources.Sum(s => s.Item2.Count);
            StatusText = $"Recalculating Coulomb field ({totalQ:N0} charges)...";

            var field = await Task.Run(() =>
                CoulombSolver.ComputeField(sources, gridMin, gridMax,
                    _coulombGridDensity));

            var entry = new SceneEntry
            {
                Kind = SceneEntryKind.Static,
                Name = $"Coulomb E ({totalQ} charges, {_coulombGridDensity}³) [auto]",
                Field = field,
                FieldAccessor = field.BuildStructuredAccessor(),
                IsCoulombDerived = true
            };
            entry.PropertyChanged += OnEntryPropertyChanged;
            SceneEntries.Add(entry);

            StatusText = $"Coulomb field recalculated.";
            RebuildScene();
            UpdateFieldSlice();
        }
        catch (Exception ex)
        {
            StatusText = $"Coulomb recalc error: {ex.Message}";
        }
        finally
        {
            _coulombRecalcInFlight = false;
            _coulombComputeInProgress = false;

            // If another recalc was requested while we were busy, do it now
            if (_coulombRecalcPending)
            {
                _coulombRecalcPending = false;
                RecalcCoulombField();
            }
        }
    }

    private async void OnCalcParticleField()
    {
        var sources = SceneEntries
            .Where(e => e.Particles != null && e.ShapeParams != null && e.IsVisible)
            .Select(e => (e, e.Particles!))
            .ToList();

        if (sources.Count == 0)
        {
            StatusText = "No particles to compute field from. Populate first.";
            return;
        }

        var bounds = GetImportedFieldBounds();
        if (bounds == null)
        {
            StatusText = "No imported E-field to define grid bounds. Import a field first.";
            return;
        }

        RemoveCoulombEntries();
        UpdateFieldSlice();  // show clean tet-only

        var (gridMin, gridMax) = bounds.Value;
        int totalQ = sources.Sum(s => s.Item2.Count);
        StatusText = $"Computing Coulomb field from {totalQ:N0} charges on " +
                     $"{_coulombGridDensity}³ grid...";
        ParticleStatusText = "Calculating...";

        var progress = new Progress<double>(p => PopulateProgress = p * 100);

        // In OnCalcParticleField, before calling CoulombSolver.ComputeField:
        foreach (var (entry, cloud) in sources)
        {
            var positions = cloud.Positions;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in positions)
            {
                var worldP = Vector3.Transform(p, entry.Transform.ToMatrix4x4());
                if (worldP.Y < minY) minY = worldP.Y;
                if (worldP.Y > maxY) maxY = worldP.Y;
                if (worldP.Z < minZ) minZ = worldP.Z;
                if (worldP.Z > maxZ) maxZ = worldP.Z;
            }
            Console.WriteLine($"[OnCalcParticleField] '{entry.Name}' world particle extent:");
            Console.WriteLine($"  Y: [{minY:F4}, {maxY:F4}] (span {maxY - minY:F4})");
            Console.WriteLine($"  Z: [{minZ:F4}, {maxZ:F4}] (span {maxZ - minZ:F4})");
        }

        try
        {
            var field = await Task.Run(() =>
                CoulombSolver.ComputeField(sources, gridMin, gridMax,
                    _coulombGridDensity, progress));

            var entry = new SceneEntry
            {
                Kind = SceneEntryKind.Static,
                Name = $"Coulomb E ({totalQ} charges, {_coulombGridDensity}³)",
                Field = field,
                FieldAccessor = field.BuildStructuredAccessor(),
                IsCoulombDerived = true
            };
            SceneEntries.Add(entry);

            PopulateProgress = 100;
            ParticleStatusText = $"Computed E-field: {field.PointCount:N0} grid points, " +
                                 $"from {totalQ:N0} charges. Grid matched to imported fields.";
            StatusText = $"Coulomb field added as '{entry.Name}'.";

            RebuildScene();
            UpdateFieldSlice();
        }
        catch (Exception ex)
        {
            StatusText = $"Coulomb computation error: {ex.Message}";
            ParticleStatusText = "Calculation failed.";
        }
    }

    private void RemoveCoulombEntries()
    {
        var toRemove = SceneEntries.Where(e => e.IsCoulombDerived).ToList();
        foreach (var e in toRemove)
            SceneEntries.Remove(e);
    }

    private void OnClearParticles()
    {
        foreach (var entry in SceneEntries)
        {
            if (entry.Particles != null)
            {
                entry.Particles = null;
                entry.NotifyContentChanged();
            }
        }

        RemoveCoulombEntries();

        IsPopulated = false;
        PopulateProgress = 0;
        _coulombRecalcPending = false;
        ParticleStatusText = "Cleared. All objects unlocked.";
        StatusText = "Particles and Coulomb fields cleared.";
        RebuildScene();
        UpdateFieldSlice();
    }

    // ── Probe: add / delete ──────────────────────────────────

    private void OnAddPointProbe()
    {
        var probe = new ProbeDefinition
        {
            Type = ProbeType.Point,
            Name = $"Point Probe {Probes.Count + 1}"
        };
        probe.PropertyChanged += OnProbePropertyChanged;
        Probes.Add(probe);
        SelectedProbe = probe;
        StatusText = $"Added {probe.Name}. Set coordinates, then Evaluate.";
        RebuildScene();
    }

    private void OnAddLineProbe()
    {
        var probe = new ProbeDefinition
        {
            Type = ProbeType.LineSegment,
            Name = $"Line Probe {Probes.Count + 1}",
            BX = 1f  // default: unit line along X
        };
        probe.PropertyChanged += OnProbePropertyChanged;
        Probes.Add(probe);
        SelectedProbe = probe;
        StatusText = $"Added {probe.Name}. Set A/B coordinates, then Evaluate.";
        RebuildScene();
    }

    private void OnDeleteProbe()
    {
        if (_selectedProbe == null) return;
        var name = _selectedProbe.Name;
        _selectedProbe.PropertyChanged -= OnProbePropertyChanged;
        Probes.Remove(_selectedProbe);
        SelectedProbe = Probes.FirstOrDefault();
        StatusText = $"Deleted probe: {name}";
        RebuildScene();
    }

    private void OnProbePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Rebuild scene when probe position changes so markers move in real-time
        if (e.PropertyName is nameof(ProbeDefinition.AX) or nameof(ProbeDefinition.AY)
            or nameof(ProbeDefinition.AZ) or nameof(ProbeDefinition.BX)
            or nameof(ProbeDefinition.BY) or nameof(ProbeDefinition.BZ)
            or nameof(ProbeDefinition.IsVisible))
        {
            RebuildScene();
        }
    }

    // ── Probe: evaluate ──────────────────────────────────────

    private async void OnEvaluateProbes()
    {
        if (Probes.Count == 0)
        {
            StatusText = "No probes defined. Add a point or line probe first.";
            return;
        }
        if (_probeEvalInFlight)
        {
            StatusText = "Probe evaluation already in progress.";
            return;
        }

        _probeEvalInFlight = true;
        var entries = SceneEntries.ToList();
        var probesToEval = Probes.ToList();
        int steps = _voltageIntegrationSteps;
        int total = probesToEval.Count;

        StatusText = $"Evaluating {total} probe(s)…";
        PopulateProgress = 0;

        try
        {
            int done = 0;
            foreach (var probe in probesToEval)
            {
                var localProbe = probe;
                var progress = new Progress<double>(p =>
                    PopulateProgress = ((done + p) / total) * 100);

                var result = await Task.Run(() =>
                    ProbeSolver.Evaluate(localProbe, entries, steps, progress));

                localProbe.Result = result;
                done++;
            }

            PopulateProgress = 100;
            UpdateProbeResultDisplay();
            StatusText = $"Evaluated {total} probe(s) successfully.";
            RebuildScene();

            // Auto-switch to graph tab for line probes
            if (_selectedProbe?.Type == ProbeType.LineSegment &&
                _selectedProbe.Result != null)
            {
                CenterTabIndex = 1;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Probe evaluation error: {ex.Message}";
        }
        finally
        {
            _probeEvalInFlight = false;
        }
    }

    /// <summary>
    /// Builds an OxyPlot PlotModel from the selected line probe's results.
    /// Shows |E| on the left axis and V on the right axis, each split by source category.
    /// </summary>
    private void UpdateProbePlot()
    {
        if (_selectedProbe?.Type != ProbeType.LineSegment ||
            _selectedProbe.Result == null ||
            _selectedProbe.Result.Samples.Length < 2)
        {
            ProbePlotModel = null;
            return;
        }

        var samples = _selectedProbe.Result.Samples;
        float lineLen = Vector3.Distance(_selectedProbe.PointA, _selectedProbe.PointB);

        // ── Dark-themed plot model ──
        var model = new PlotModel
        {
            Title = _selectedProbe.Name,
            Subtitle = $"{samples.Length} samples · {lineLen:F4} m",
            Background = OxyColor.FromRgb(26, 26, 40),
            PlotAreaBackground = OxyColor.FromRgb(20, 20, 34),
            TextColor = OxyColor.FromRgb(176, 176, 200),
            PlotAreaBorderColor = OxyColor.FromRgb(64, 64, 88),
            TitleColor = OxyColor.FromRgb(210, 210, 230),
            SubtitleColor = OxyColor.FromRgb(120, 120, 150),
            TitleFontWeight = 700,
            TitleFontSize = 14,
            SubtitleFontSize = 11,
            Padding = new OxyThickness(8, 8, 16, 8),
            PlotMargins = new OxyThickness(60, 10, 60, 40),
        };

        // Legend
        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.TopRight,
            LegendPlacement = LegendPlacement.Inside,
            LegendBackground = OxyColor.FromArgb(180, 30, 30, 48),
            LegendBorder = OxyColor.FromRgb(64, 64, 88),
            LegendTextColor = OxyColor.FromRgb(190, 190, 210),
            LegendFontSize = 10,
            LegendPadding = 6,
            LegendItemSpacing = 4,
        });

        // ── Axes ──
        var axisStyleBase = new
        {
            TicklineColor = OxyColor.FromRgb(80, 80, 100),
            AxislineColor = OxyColor.FromRgb(80, 80, 100),
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromArgb(35, 128, 140, 200),
            MinorGridlineStyle = LineStyle.Dot,
            MinorGridlineColor = OxyColor.FromArgb(18, 128, 140, 200),
            AxislineStyle = LineStyle.Solid,
        };

        var xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Distance along line (m)",
            Key = "x",
            TitleColor = OxyColor.FromRgb(160, 170, 200),
            TextColor = OxyColor.FromRgb(140, 140, 165),
            TicklineColor = axisStyleBase.TicklineColor,
            AxislineColor = axisStyleBase.AxislineColor,
            AxislineStyle = axisStyleBase.AxislineStyle,
            MajorGridlineStyle = axisStyleBase.MajorGridlineStyle,
            MajorGridlineColor = axisStyleBase.MajorGridlineColor,
            MinorGridlineStyle = axisStyleBase.MinorGridlineStyle,
            MinorGridlineColor = axisStyleBase.MinorGridlineColor,
            TitleFontSize = 12,
            FontSize = 10,
            Minimum = 0,
            Maximum = lineLen > 0 ? lineLen : 1,
        };
        model.Axes.Add(xAxis);

        var eAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "|E|  (V/m)",
            Key = "e",
            TitleColor = OxyColor.FromRgb(180, 190, 220),
            TextColor = OxyColor.FromRgb(140, 150, 180),
            TicklineColor = axisStyleBase.TicklineColor,
            AxislineColor = OxyColor.FromRgb(80, 110, 170),
            AxislineStyle = axisStyleBase.AxislineStyle,
            MajorGridlineStyle = axisStyleBase.MajorGridlineStyle,
            MajorGridlineColor = axisStyleBase.MajorGridlineColor,
            MinorGridlineStyle = LineStyle.None,
            StringFormat = "E2",
            TitleFontSize = 12,
            FontSize = 10,
        };
        model.Axes.Add(eAxis);

        var vAxis = new LinearAxis
        {
            Position = AxisPosition.Right,
            Title = "V  (V)",
            Key = "v",
            TitleColor = OxyColor.FromRgb(200, 180, 140),
            TextColor = OxyColor.FromRgb(170, 150, 120),
            TicklineColor = axisStyleBase.TicklineColor,
            AxislineColor = OxyColor.FromRgb(170, 130, 60),
            AxislineStyle = axisStyleBase.AxislineStyle,
            MajorGridlineStyle = LineStyle.None,
            StringFormat = "E2",
            TitleFontSize = 12,
            FontSize = 10,
        };
        model.Axes.Add(vAxis);

        // ── Colour palette ──
        var colTotal = OxyColor.FromRgb(220, 220, 235);
        var colStatic = OxyColor.FromRgb(80, 150, 230);
        var colParticle = OxyColor.FromRgb(230, 165, 55);
        var colTotalV = OxyColor.FromRgb(195, 195, 210);
        var colStaticV = OxyColor.FromRgb(65, 130, 210);
        var colParticleV = OxyColor.FromRgb(210, 145, 40);

        // ── E-field series (left axis, solid) ──
        if (_showProbeGraphETotal)
        {
            var s = new LineSeries
            {
                Title = "|E| Total",
                YAxisKey = "e",
                XAxisKey = "x",
                Color = colTotal,
                StrokeThickness = 2.5,
                LineStyle = LineStyle.Solid,
            };
            foreach (var pt in samples)
                s.Points.Add(new DataPoint(pt.Distance, pt.TotalFieldMagnitude));
            model.Series.Add(s);
        }

        if (_showProbeGraphEStatic)
        {
            var s = new LineSeries
            {
                Title = "|E| Static",
                YAxisKey = "e",
                XAxisKey = "x",
                Color = colStatic,
                StrokeThickness = 1.8,
                LineStyle = LineStyle.Dash,
            };
            foreach (var pt in samples)
                s.Points.Add(new DataPoint(pt.Distance, pt.StaticFieldMagnitude));
            model.Series.Add(s);
        }

        if (_showProbeGraphEParticle)
        {
            var s = new LineSeries
            {
                Title = "|E| Particles",
                YAxisKey = "e",
                XAxisKey = "x",
                Color = colParticle,
                StrokeThickness = 1.8,
                LineStyle = LineStyle.DashDot,
            };
            foreach (var pt in samples)
                s.Points.Add(new DataPoint(pt.Distance, pt.ParticleFieldMagnitude));
            model.Series.Add(s);
        }

        // ── Voltage series (right axis, dotted/dashed) ──
        if (_showProbeGraphVTotal)
        {
            var s = new LineSeries
            {
                Title = "V Total",
                YAxisKey = "v",
                XAxisKey = "x",
                Color = colTotalV,
                StrokeThickness = 2.5,
                LineStyle = LineStyle.LongDash,
            };
            foreach (var pt in samples)
                s.Points.Add(new DataPoint(pt.Distance, pt.TotalVoltage));
            model.Series.Add(s);
        }

        if (_showProbeGraphVStatic)
        {
            var s = new LineSeries
            {
                Title = "V Static",
                YAxisKey = "v",
                XAxisKey = "x",
                Color = colStaticV,
                StrokeThickness = 1.5,
                LineStyle = LineStyle.LongDashDot,
            };
            foreach (var pt in samples)
                s.Points.Add(new DataPoint(pt.Distance, pt.StaticVoltage));
            model.Series.Add(s);
        }

        if (_showProbeGraphVParticle)
        {
            var s = new LineSeries
            {
                Title = "V Particles",
                YAxisKey = "v",
                XAxisKey = "x",
                Color = colParticleV,
                StrokeThickness = 1.5,
                LineStyle = LineStyle.LongDashDotDot,
            };
            foreach (var pt in samples)
                s.Points.Add(new DataPoint(pt.Distance, pt.ParticleVoltage));
            model.Series.Add(s);
        }

        // ── Per-source detail series (thinner, semi-transparent) ──
        // If there are multiple distinct sources, add individual traces
        if (samples.Length > 0 && samples[0].Contributions.Length > 1)
        {
            int ci = 0;
            byte[] hues = { 120, 200, 50, 170, 90, 240 }; // spread across palette
            foreach (var contrib in samples[0].Contributions)
            {
                byte hue = hues[ci % hues.Length];
                var seriesColor = OxyColor.FromAColor(
                    120,
                    contrib.IsCoulombDerived
                        ? OxyColor.FromHsv(hue / 360.0, 0.6, 0.85)
                        : OxyColor.FromHsv(hue / 360.0, 0.4, 0.90));

                var name = contrib.SourceName;
                var idx = ci;

                // |E| per source
                var eSeries = new LineSeries
                {
                    Title = $"|E| {name}",
                    YAxisKey = "e",
                    XAxisKey = "x",
                    Color = seriesColor,
                    StrokeThickness = 1.0,
                    LineStyle = LineStyle.Dot,
                    IsVisible = false, // hidden by default, click legend to show
                };
                foreach (var pt in samples)
                {
                    float mag = idx < pt.Contributions.Length
                        ? pt.Contributions[idx].FieldMagnitude
                        : 0f;
                    eSeries.Points.Add(new DataPoint(pt.Distance, mag));
                }
                model.Series.Add(eSeries);
                ci++;
            }
        }

        ProbePlotModel = model;
    }

    // ── Probe: result display helpers ────────────────────────

    private void UpdateProbeResultDisplay()
    {
        ProbeLineSamples.Clear();

        if (_selectedProbe?.Result == null)
        {
            ProbeResultText = _selectedProbe != null
                ? "Not yet evaluated. Click ⚡ Evaluate Probes."
                : "No probe selected.";
            UpdateProbePlot();
            return;
        }

        var result = _selectedProbe.Result;

        if (_selectedProbe.Type == ProbeType.Point && result.Samples.Length > 0)
        {
            ProbeResultText = FormatPointProbeResult(_selectedProbe, result.Samples[0]);
        }
        else if (_selectedProbe.Type == ProbeType.LineSegment)
        {
            var first = result.Samples.FirstOrDefault();
            var last = result.Samples.LastOrDefault();
            if (first != null && last != null)
            {
                ProbeResultText =
                    $"Line: {result.Samples.Length} samples, " +
                    $"length {Vector3.Distance(_selectedProbe.PointA, _selectedProbe.PointB):F4} m\n" +
                    $"|E| range: {result.Samples.Min(s => s.TotalFieldMagnitude):E3} – " +
                    $"{result.Samples.Max(s => s.TotalFieldMagnitude):E3} V/m\n" +
                    $"V range: {result.Samples.Min(s => s.TotalVoltage):E3} – " +
                    $"{result.Samples.Max(s => s.TotalVoltage):E3} V";
            }

            foreach (var s in result.Samples)
                ProbeLineSamples.Add(s);
        }
        else
        {
            ProbeResultText = "No data.";
        }

        UpdateProbePlot();
    }

    private static string FormatPointProbeResult(ProbeDefinition probe, ProbePointSample s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Point: ({probe.AX:F4}, {probe.AY:F4}, {probe.AZ:F4})");
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"|E| Total:    {s.TotalFieldMagnitude:E4} V/m");
        sb.AppendLine($"  Ex={s.TotalField.X:E3}  Ey={s.TotalField.Y:E3}  Ez={s.TotalField.Z:E3}");
        sb.AppendLine($"|E| Static:   {s.StaticFieldMagnitude:E4} V/m");
        sb.AppendLine($"|E| Particles:{s.ParticleFieldMagnitude:E4} V/m");
        sb.AppendLine();
        sb.AppendLine($"V Total:      {s.TotalVoltage:E4} V");
        sb.AppendLine($"V Static:     {s.StaticVoltage:E4} V");
        sb.AppendLine($"V Particles:  {s.ParticleVoltage:E4} V");

        if (s.Contributions.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("── Per-Source Breakdown ──");
            foreach (var c in s.Contributions)
            {
                sb.AppendLine($"  [{c.SourceCategory}] {c.SourceName}");
                sb.AppendLine($"    |E|={c.FieldMagnitude:E3} V/m  V={c.VoltageContribution:E3} V");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── Field source helpers ─────────────────────────────────

    private List<SceneEntry> GetFieldEntries() =>
        SceneEntries.Where(e => e.Field != null && e.IsVisible).ToList();

    /// <summary>
    /// Returns world-space union bounding box of all imported (non-Coulomb) field entries,
    /// accounting for their current transforms.
    /// </summary>
    private (Vector3 min, Vector3 max)? GetImportedFieldBounds()
    {
        // Prefer imported (non-Coulomb) fields for grid bounds
        var candidates = SceneEntries
            .Where(e => e.Field != null && e.IsVisible && !e.IsCoulombDerived)
            .ToList();

        // Fall back to Coulomb-derived if no imports exist
        if (candidates.Count == 0)
            candidates = SceneEntries
                .Where(e => e.Field != null && e.IsVisible)
                .ToList();

        if (candidates.Count == 0) return null;

        Vector3 unionMin = new(float.MaxValue);
        Vector3 unionMax = new(float.MinValue);

        foreach (var e in candidates)
        {
            var (fmin, fmax) = e.Field!.GetBounds();
            var mat = e.Transform.ToMatrix4x4();
            Vector3[] corners =
            {
            new(fmin.X, fmin.Y, fmin.Z), new(fmax.X, fmin.Y, fmin.Z),
            new(fmin.X, fmax.Y, fmin.Z), new(fmax.X, fmax.Y, fmin.Z),
            new(fmin.X, fmin.Y, fmax.Z), new(fmax.X, fmin.Y, fmax.Z),
            new(fmin.X, fmax.Y, fmax.Z), new(fmax.X, fmax.Y, fmax.Z),
        };
            foreach (var c in corners)
            {
                var tc = Vector3.Transform(c, mat);
                unionMin = Vector3.Min(unionMin, tc);
                unionMax = Vector3.Max(unionMax, tc);
            }
        }

        return unionMin.X < unionMax.X ? (unionMin, unionMax) : null;
    }

    // ── Transform / update ───────────────────────────────────

    private void OnMovableTransformChanged()
    {
        if (_isPopulated && _selectedEntry != null &&
            _selectedEntry.Kind != SceneEntryKind.Movable)
            return;

        _updatePending = true;
        RebuildScene();

        // If populated and a Coulomb field exists, schedule recalculation
        if (_isPopulated && HasCoulombEntries())
            _coulombRecalcPending = true;

        // Voltage results become stale when fields move
        foreach (var e in SceneEntries)
            if (e.Kind == SceneEntryKind.VoltageSurface && e.VoltageResult != null)
            { e.VoltageResult = null; e.NotifyContentChanged(); }
    }
    private bool HasCoulombEntries() =>
        SceneEntries.Any(e => e.IsCoulombDerived);

    private void PerformUpdate() => UpdateFieldSlice();
    private void UpdateSliceAxisName() =>
        SliceAxisName = _sliceAxis switch { 0 => "X", 1 => "Y", _ => "Z" };

    // ── 3D scene ─────────────────────────────────────────────

    private void RebuildScene()
    {
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(60, 60, 60)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(200, 200, 200), new Vector3D(-1, -1, -1)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(100, 100, 120), new Vector3D(1, 0.5, 0.5)));

        foreach (var entry in SceneEntries)
        {
            if (!entry.IsVisible) continue;

            if (entry.Mesh != null)
                group.Children.Add(BuildMeshModel(entry));

            if (_showShape && entry.ShapeParams != null
                && entry.Kind == SceneEntryKind.ChargeVolume)
                group.Children.Add(ShapeGenerator.CreateShapeModel(
                    entry.ShapeParams,
                    Color.FromRgb(80, 200, 120), 0.4,
                    entry.Transform));

            if (_showVoltageSurfaces && entry.Kind == SceneEntryKind.VoltageSurface
                && entry.ShapeParams != null)
            {
                if (entry.VoltageResult != null)
                {
                    // Colour-mapped voltage distribution in 3D
                    group.Children.Add(
                        Rendering.VoltageSurfaceRenderer.CreateModel(entry.VoltageResult));
                }
                else
                {
                    // Not calculated yet → neutral grey placeholder
                    group.Children.Add(ShapeGenerator.CreateShapeModel(
                        entry.ShapeParams,
                        Color.FromRgb(150, 150, 160), 0.30,
                        entry.Transform));
                }
            }

            // Particles
            if (_showParticles && entry.Particles != null && entry.ShapeParams != null)
            {
                group.Children.Add(ParticleRenderer.CreateParticleModel(
                    entry.Particles,
                    entry.ShapeParams,
                    entry.Transform));
            }
        }

        var fieldEntries = GetFieldEntries();
        if (_showFieldArrows && fieldEntries.Count > 0)
            AddFieldArrows(group, fieldEntries);

        // ── Probe markers in 3D ──
        if (_showProbes && Probes.Count > 0)
        {
            // Compute marker size relative to scene (~1% of field diagonal)
            double markerSize = 0.06;
            var fb = GetImportedFieldBounds();
            if (fb.HasValue)
                markerSize = Vector3.Distance(fb.Value.min, fb.Value.max) * 0.012;
            markerSize = Math.Clamp(markerSize, 0.01, 0.5);

            Rendering.ProbeRenderer.AddProbeMarkers(group, Probes, markerSize);
        }

        SceneGroup = group;
    }

   // Converts MeshData to WPF geometry once and reuses it on future scene rebuilds.
    private MeshGeometry3D GetCachedMeshGeometry(MeshData mesh)
    {
        if (_meshGeometryCache.TryGetValue(mesh, out var cached))
            return cached;

        var geo = mesh.ToMeshGeometry3D();

        if (geo.CanFreeze)
            geo.Freeze();

        _meshGeometryCache[mesh] = geo;
        return geo;
    }

    // Builds the visible 3D model using cached mesh geometry while still applying each entry's current transform.
    private GeometryModel3D BuildMeshModel(SceneEntry entry)
    {
        bool movable = entry.Kind == SceneEntryKind.Movable;
        var color = movable ? Color.FromArgb(200, 220, 150, 60) : Color.FromArgb(200, 100, 130, 180);
        var back = movable ? Color.FromArgb(150, 180, 120, 40) : Color.FromArgb(150, 80, 100, 140);

        var geo = GetCachedMeshGeometry(entry.Mesh!);

        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        mat.Children.Add(new SpecularMaterial(Brushes.White, 30));

        return new GeometryModel3D
        {
            Geometry = geo,
            Material = mat,
            BackMaterial = new DiffuseMaterial(new SolidColorBrush(back)),
            Transform = entry.Transform.ToWpfTransform()
        };
    }
    private async void OnCalcPathlines()
    {
        var entries = GetFieldEntries();
        if (entries.Count == 0)
        {
            StatusText = "No E-field entries visible. Load or calculate a field first.";
            return;
        }

        var normal = new System.Numerics.Vector3(
            (float)_pathlineNormalX, (float)_pathlineNormalY, (float)_pathlineNormalZ);
        if (normal.Length() < 1e-6f)
        {
            StatusText = "Pathline plane normal is zero — set at least one normal component.";
            return;
        }

        var p = new PathlineParams
        {
            PlaneCenter = new System.Numerics.Vector3(
                (float)_pathlinePlaneCenterX, (float)_pathlinePlaneCenterY, (float)_pathlinePlaneCenterZ),
            PlaneNormal = normal,
            PlaneWidth = (float)_pathlinePlaneWidth,
            PlaneHeight = (float)_pathlinePlaneHeight,
            GridDensity = _pathlineGridDensity,
            InitialSpeed = (float)_pathlineInitialSpeed,
            IsElectron = _pathlineIsElectron,
            MaxSteps = _pathlineMaxSteps,
            TimeStep = (float)_pathlineTimeStep,
        };

        StatusText = $"Computing {p.GridDensity * p.GridDensity} pathlines…";
        var capturedEntries = entries.ToList();

        List<List<System.Numerics.Vector3>> paths;
        try
        {
            paths = await Task.Run(() => PathlineSolver.Compute(capturedEntries, p));
        }
        catch (Exception ex) { StatusText = $"Pathline error: {ex.Message}"; return; }

        // Build flat line-segment pair collection for helix:LinesVisual3D
        var col = new System.Windows.Media.Media3D.Point3DCollection();
        foreach (var path in paths)
            for (int i = 0; i < path.Count - 1; i++)
            {
                col.Add(new System.Windows.Media.Media3D.Point3D(path[i].X, path[i].Y, path[i].Z));
                col.Add(new System.Windows.Media.Media3D.Point3D(path[i + 1].X, path[i + 1].Y, path[i + 1].Z));
            }
        col.Freeze();
        PathlinePoints = col;
        OnPropertyChanged(nameof(PathlineParticleColor));

        int totalSeg = col.Count / 2;
        StatusText = $"Pathlines: {paths.Count} tracks, {totalSeg:N0} segments " +
                     $"({(_pathlineIsElectron ? "electrons" : "protons")}).";
    }

    private void AddFieldArrows(Model3DGroup group, List<SceneEntry> fieldEntries)
    {
        int n = _arrowGridDensity;
        Vector3 unionMin = new(float.MaxValue), unionMax = new(float.MinValue);

        foreach (var e in fieldEntries)
        {
            var (smin, smax) = e.Field!.GetBounds();
            var mat = e.Transform.ToMatrix4x4();
            Vector3[] corners = {
                Vector3.Transform(smin, mat), Vector3.Transform(smax, mat),
                Vector3.Transform(new(smin.X, smin.Y, smax.Z), mat),
                Vector3.Transform(new(smax.X, smax.Y, smin.Z), mat),
            };
            foreach (var c in corners)
            { unionMin = Vector3.Min(unionMin, c); unionMax = Vector3.Max(unionMax, c); }
        }

        if (unionMin.X >= unionMax.X) return;
        Vector3 range = unionMax - unionMin;
        Vector3 step = range / (n - 1);

        float maxMag = 0.001f;
        var arrowData = new List<(Vector3 pos, Vector3 field)>();

        for (int ix = 0; ix < n; ix++)
        for (int iy = 0; iy < n; iy++)
        for (int iz = 0; iz < n; iz++)
        {
            Vector3 pos = unionMin + new Vector3(ix * step.X, iy * step.Y, iz * step.Z);
            Vector3 total = Vector3.Zero;
            foreach (var e in fieldEntries)
                total += FieldSuperposition.SampleEntry(e, pos);
            float mag = total.Length();
            if (mag > 1e-10f) { arrowData.Add((pos, total)); if (mag > maxMag) maxMag = mag; }
        }

        float arrowLen = step.Length() * 0.4f;
        foreach (var (pos, field) in arrowData)
        {
            float mag = field.Length();
            float nm = mag / maxMag;
            var dir = Vector3.Normalize(field);
            var (r, g, b) = MathHelpers.JetColorMap(nm);
            group.Children.Add(new GeometryModel3D
            {
                Geometry = CreateArrowMesh(new Point3D(pos.X, pos.Y, pos.Z),
                    new Vector3D(dir.X, dir.Y, dir.Z), arrowLen * nm, arrowLen * 0.03f),
                Material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(r, g, b)))
            });
        }
    }

    private static MeshGeometry3D CreateArrowMesh(
        Point3D origin, Vector3D direction, double length, double thickness)
    {
        var mesh = new MeshGeometry3D();
        Vector3D up = Math.Abs(direction.Y) < 0.9 ? new Vector3D(0,1,0) : new Vector3D(1,0,0);
        Vector3D right = Vector3D.CrossProduct(direction, up); right.Normalize();
        up = Vector3D.CrossProduct(right, direction); up.Normalize();
        Point3D tip = origin + direction * length;
        double t = thickness;
        Point3D[] bc = {
            origin+right*t+up*t, origin-right*t+up*t,
            origin-right*t-up*t, origin+right*t-up*t };
        Point3D[] tc = {
            tip+right*t*0.3+up*t*0.3, tip-right*t*0.3+up*t*0.3,
            tip-right*t*0.3-up*t*0.3, tip+right*t*0.3-up*t*0.3 };
        for (int i = 0; i < 4; i++) { mesh.Positions.Add(bc[i]); mesh.Positions.Add(tc[i]); }
        for (int i = 0; i < 4; i++)
        {
            int bl=i*2, tl=i*2+1, br=((i+1)%4)*2, tr=((i+1)%4)*2+1;
            mesh.TriangleIndices.Add(bl); mesh.TriangleIndices.Add(br); mesh.TriangleIndices.Add(tl);
            mesh.TriangleIndices.Add(tl); mesh.TriangleIndices.Add(br); mesh.TriangleIndices.Add(tr);
        }
        double headLen = length * 0.25;
        Point3D headBase = origin + direction * (length - headLen);
        double headR = thickness * 2.5;
        int baseIdx = mesh.Positions.Count;
        mesh.Positions.Add(tip); int tipIdx = baseIdx; int seg = 6;
        for (int i = 0; i <= seg; i++)
        {
            double a = 2*Math.PI*i/seg;
            mesh.Positions.Add(headBase + right*(headR*Math.Cos(a)) + up*(headR*Math.Sin(a)));
        }
        for (int i = 0; i < seg; i++)
        { mesh.TriangleIndices.Add(tipIdx); mesh.TriangleIndices.Add(baseIdx+1+i); mesh.TriangleIndices.Add(baseIdx+2+i); }
        return mesh;
    }

    // ── 2D slice ─────────────────────────────────────────────

    private void UpdateFieldSlice()
    {
        var entries = GetFieldEntries();
        if (entries.Count == 0) { FieldSliceImage = null; return; }
        UpdateSliceAxisName();

        // Capture locals for the background closure
        int axis = _sliceAxis;
        float pos = _slicePosition;
        int res = _sliceResolution;
        bool doZero = _showZeroContour;
        float zThresh = _zeroFieldThresholdPercent;
        double emaxPct = _emaxDisplayPercent;

        // Capture the entries list and superposition reference for direct sampling
        var capturedEntries = entries.ToList().AsReadOnly();
        var superposition = _superposition;

        Task.Run(() =>
        {
            try
            {
                var result = superposition.ComputeSlice(axis, pos, res, capturedEntries);

                // ═══ FIELD DIRECTION DIAGNOSTIC ═══
                var tetEntry = capturedEntries.FirstOrDefault(e => !e.IsCoulombDerived);
                var coulombEntry = capturedEntries.FirstOrDefault(e => e.IsCoulombDerived);

                if (tetEntry != null && coulombEntry != null)
                {
                    // Pick a test point in the hollow, off-axis, mid-height.
                    // Adjust these coordinates for your geometry!
                    var testPt = new Vector3(0.3f * result.Axis0Max, 0f, 0f);

                    var eTet = FieldSuperposition.SampleEntry(tetEntry, testPt);
                    var eCoul = FieldSuperposition.SampleEntry(coulombEntry, testPt);
                    var radial = Vector3.Normalize(testPt);  // radially outward unit vector
                }

                ZeroFieldFinder.ZeroFieldResult? zeroResult = null;
                if (doZero)
                {
                    // Build a direct-sampling geometry so Newton refinement
                    // bypasses the pre-computed slice grid entirely.
                    // This eliminates the double-interpolation error that was
                    // preventing accurate E=0 detection in superposed fields.
                    var geometry = FieldSuperposition.CreateSliceGeometry(result, capturedEntries);

                    zeroResult = ZeroFieldFinder.Find(
                        result,
                        zThresh / 100f,
                        newtonIterations: 16,
                        geometry: geometry,
                        useRobustPercentile: true);
                }

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    RenderSliceToBitmap(result, emaxPct, zeroResult);

                    if (zeroResult != null)
                    {
                        int nPts = zeroResult.Points.Count;
                        int nFull = 0;
                        foreach (var p in zeroResult.Points)
                            if (p.IsFullZero) nFull++;

                        float refMag = zeroResult.ReferenceMagnitude;
                        float effectiveThreshPct = refMag > 0
                            ? zeroResult.ThresholdUsed / refMag * 100f : 0f;

                        ZeroPointStatusText = nPts == 0
                            ? $"No E≈0 points on this slice (threshold {effectiveThreshPct:F2}% of ref |E|={refMag:E2})."
                            : $"{nPts} E≈0 point(s) found ({nFull} full 3-component zero), " +
                              $"{zeroResult.Contour.Count} contour segments. " +
                              $"Threshold: {zeroResult.ThresholdUsed:E2} V/m " +
                              $"({effectiveThreshPct:F2}% of ref {refMag:E2}).";
                    }
                    else
                    {
                        ZeroPointStatusText = string.Empty;
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.Invoke(
                    () => StatusText = $"Slice error: {ex.Message}");
            }
        });
    }

    private void RenderSliceToBitmap(
    SliceResult result,
    double emaxPct,
    ZeroFieldFinder.ZeroFieldResult? zeroResult = null)
    {
        int res = result.Resolution;
        var bmp = new WriteableBitmap(res, res, 96, 96, PixelFormats.Bgra32, null);
        int stride = res * 4;
        byte[] px = new byte[res * res * 4];

        float ceil = result.MaxMagnitude * (float)(emaxPct / 100.0);
        if (ceil < 1e-20f) ceil = 1f;

        // Base magnitude heatmap (unchanged logic)
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                float mag = result.Magnitudes[x, res - 1 - y];
                float t = Math.Clamp(mag / ceil, 0f, 1f);
                var (r, g, b) = MathHelpers.JetColorMap(t);
                int i = (y * res + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;

                // Existing directional shading
                var f = result.FieldValues[x, res - 1 - y];
                if (f.Length() > 1e-10f)
                {
                    var d = Vector3.Normalize(f);
                    px[i] = (byte)Math.Clamp(
                        px[i] * (0.8f + 0.2f * (d.X + 1) * 0.5f), 0, 255);
                }
            }

        // Zero-field overlay (contour + markers)
        if (zeroResult != null)
            ZeroFieldFinder.OverlayOnPixels(px, res, zeroResult);

        bmp.WritePixels(new Int32Rect(0, 0, res, res), px, stride, 0);
        FieldSliceImage = bmp;
    }

    private async void OnBakeHighResSlice()
    {
        var entries = GetFieldEntries();
        if (entries.Count == 0)
        {
            StatusText = "No field data to bake.";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save High-Resolution Field Slice",
            Filter = "PNG Image|*.png",
            FileName = $"EFieldSlice_{_sliceAxisName}{_slicePosition:F3}_{_bakeResolution}px.png"
        };
        if (dlg.ShowDialog() != true) return;

        string path = dlg.FileName;
        int res = _bakeResolution;
        int axis = _sliceAxis;
        float pos = _slicePosition;
        float zThresh = _zeroFieldThresholdPercent / 100f;
        bool doZero = _showZeroContour;
        double emaxPct = _emaxDisplayPercent;

        var capturedEntries = entries.ToList().AsReadOnly();
        var superposition = _superposition;

        StatusText = $"Baking {res}×{res} slice...";

        try
        {
            await Task.Run(() =>
            {
                var result = superposition.ComputeSlice(axis, pos, res, capturedEntries);

                ZeroFieldFinder.ZeroFieldResult? zeroResult = null;
                if (doZero)
                {
                    var geometry = FieldSuperposition.CreateSliceGeometry(result, capturedEntries);
                    zeroResult = ZeroFieldFinder.Find(
                        result, zThresh,
                        newtonIterations: 24,
                        geometry: geometry,
                        useRobustPercentile: true);
                }

                byte[] px = new byte[res * res * 4];
                float ceil = result.MaxMagnitude * (float)(emaxPct / 100.0);
                if (ceil < 1e-20f) ceil = 1f;

                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                    {
                        float mag = result.Magnitudes[x, res - 1 - y];
                        float t = Math.Clamp(mag / ceil, 0f, 1f);
                        var (r, g, b) = MathHelpers.JetColorMap(t);
                        int i = (y * res + x) * 4;
                        px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
                    }

                if (zeroResult != null)
                    ZeroFieldFinder.OverlayOnPixels(px, res, zeroResult,
                        pointRadius: Math.Max(4, res / 200));

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var bmp = new WriteableBitmap(res, res, 96, 96,
                        PixelFormats.Bgra32, null);
                    bmp.WritePixels(new Int32Rect(0, 0, res, res), px, res * 4, 0);

                    using var fs = new System.IO.FileStream(path,
                        System.IO.FileMode.Create);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    encoder.Save(fs);
                });
            });

            StatusText = $"Saved {res}×{res} slice to {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Bake failed: {ex.Message}";
        }
    }

    private double EstimateShapeVolume() =>
    ActiveShapeParams != null ? ShapeLibrary.EvaluateVolume(ActiveShapeParams) : 1.0;

    private static string? BrowseFile(string filter)
    { var d = new OpenFileDialog { Filter = filter }; return d.ShowDialog() == true ? d.FileName : null; }

    private void OnSaveProject()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "EField Project|*.efproj",
            DefaultExt = ".efproj",
            Title = "Save Project"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var manifest = BuildManifestFromVm();
            ProjectSerializer.Save(dlg.FileName, manifest, SceneEntries.ToList());
            StatusText = $"Project saved: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private void OnLoadProject()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "EField Project|*.efproj|All Files|*.*",
            Title = "Open Project"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var (manifest, entries) = ProjectSerializer.Load(dlg.FileName);

            // Clear current scene
        foreach (var e in SceneEntries)
            e.PropertyChanged -= OnEntryPropertyChanged;

        // Clears cached geometry from the previous project before loading the new scene entries.
        _meshGeometryCache.Clear();

        SceneEntries.Clear();
        SelectedEntry = null;
            SelectedEntry = null;
            IsPopulated = false;
            PathlinePoints = new System.Windows.Media.Media3D.Point3DCollection();

            // Restore entries
            foreach (var entry in entries)
            {
                entry.PropertyChanged += OnEntryPropertyChanged;
                SceneEntries.Add(entry);
            }

            // Restore global VM settings from manifest
            ApplyManifestToVm(manifest);

            SelectedEntry = SceneEntries.FirstOrDefault();
            RebuildScene();
            UpdateFieldSlice();
            UpdatePathlinePlaneOutline();
            StatusText = $"Project loaded: {System.IO.Path.GetFileName(dlg.FileName)} " +
                         $"({entries.Count} entries)";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    private void OnExportFieldHdf5()
    {
        if (_selectedEntry?.Field == null)
        {
            StatusText = "No field data on selected entry to export.";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "HDF5 Files|*.h5;*.hdf5",
            DefaultExt = ".h5",
            Title = "Export E-Field to HDF5",
            FileName = $"{_selectedEntry.Name.Replace(' ', '_')}_field.h5"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            FieldExporter.ExportToHdf5(dlg.FileName, _selectedEntry.Field);
            StatusText = $"Field exported: {System.IO.Path.GetFileName(dlg.FileName)} " +
                         $"({_selectedEntry.Field.PointCount:N0} points)";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private ProjectManifest BuildManifestFromVm() => new()
    {
        SliceAxis = _sliceAxis,
        SlicePosition = _slicePosition,
        SliceResolution = _sliceResolution,
        EmaxDisplayPercent = _emaxDisplayPercent,
        ArrowGridDensity = _arrowGridDensity,
        ShowFieldArrows = _showFieldArrows,
        ShowShape = _showShape,
        ShowParticles = _showParticles,
        ShowVoltageSurfaces = _showVoltageSurfaces,
        TetMaxVolumeCm3 = _tetMaxVolumeCm3,
        TetChargeDensity = _tetChargeDensity,
        TetFieldGridDensity = _tetFieldGridDensity,
        IsTetInnerPositive = _isTetInnerPositive,
        CoulombGridDensity = _coulombGridDensity,
        PathlinePlaneCenterX = _pathlinePlaneCenterX,
        PathlinePlaneCenterY = _pathlinePlaneCenterY,
        PathlinePlaneCenterZ = _pathlinePlaneCenterZ,
        PathlineNormalX = _pathlineNormalX,
        PathlineNormalY = _pathlineNormalY,
        PathlineNormalZ = _pathlineNormalZ,
        PathlinePlaneWidth = _pathlinePlaneWidth,
        PathlinePlaneHeight = _pathlinePlaneHeight,
        PathlineGridDensity = _pathlineGridDensity,
        PathlineInitialSpeed = _pathlineInitialSpeed,
        PathlineTimeStep = _pathlineTimeStep,
        PathlineMaxSteps = _pathlineMaxSteps,
        PathlineIsElectron = _pathlineIsElectron,
        VoltageIntegrationSteps = _voltageIntegrationSteps,
        Probes = Probes.Select(ProbeDto.FromDefinition).ToList()
    };

    private void ApplyManifestToVm(ProjectManifest m)
    {
        // Use property setters to trigger UI updates and dependent refreshes
        SliceAxis = m.SliceAxis;
        SlicePosition = m.SlicePosition;
        SliceResolution = m.SliceResolution;
        EmaxDisplayPercent = m.EmaxDisplayPercent;
        ArrowGridDensity = m.ArrowGridDensity;
        ShowFieldArrows = m.ShowFieldArrows;
        ShowShape = m.ShowShape;
        ShowParticles = m.ShowParticles;
        ShowVoltageSurfaces = m.ShowVoltageSurfaces;
        TetMaxVolumeCm3 = m.TetMaxVolumeCm3;
        TetChargeDensity = m.TetChargeDensity;
        TetFieldGridDensity = m.TetFieldGridDensity;
        IsTetInnerPositive = m.IsTetInnerPositive;
        CoulombGridDensity = m.CoulombGridDensity;

        // ── Restore probes ──
        foreach (var p in Probes)
            p.PropertyChanged -= OnProbePropertyChanged;
        Probes.Clear();
        SelectedProbe = null;

        if (m.Probes != null)
        {
            foreach (var dto in m.Probes)
            {
                var probe = dto.ToDefinition();
                probe.PropertyChanged += OnProbePropertyChanged;
                Probes.Add(probe);
            }
        }
        SelectedProbe = Probes.FirstOrDefault();
        UpdateProbePlot();

        // Pathline — set backing fields directly to avoid triggering
        // outline rebuild on each, then do one rebuild at the end
        _pathlinePlaneCenterX = m.PathlinePlaneCenterX;
        _pathlinePlaneCenterY = m.PathlinePlaneCenterY;
        _pathlinePlaneCenterZ = m.PathlinePlaneCenterZ;
        _pathlineNormalX = m.PathlineNormalX;
        _pathlineNormalY = m.PathlineNormalY;
        _pathlineNormalZ = m.PathlineNormalZ;
        _pathlinePlaneWidth = m.PathlinePlaneWidth;
        _pathlinePlaneHeight = m.PathlinePlaneHeight;
        _pathlineGridDensity = m.PathlineGridDensity;
        _pathlineInitialSpeed = m.PathlineInitialSpeed;
        _pathlineTimeStep = m.PathlineTimeStep;
        _pathlineMaxSteps = m.PathlineMaxSteps;
        _pathlineIsElectron = m.PathlineIsElectron;
        _voltageIntegrationSteps = m.VoltageIntegrationSteps;

        // Notify all pathline properties
        OnPropertyChanged(nameof(PathlinePlaneCenterX));
        OnPropertyChanged(nameof(PathlinePlaneCenterY));
        OnPropertyChanged(nameof(PathlinePlaneCenterZ));
        OnPropertyChanged(nameof(PathlineNormalX));
        OnPropertyChanged(nameof(PathlineNormalY));
        OnPropertyChanged(nameof(PathlineNormalZ));
        OnPropertyChanged(nameof(PathlinePlaneWidth));
        OnPropertyChanged(nameof(PathlinePlaneHeight));
        OnPropertyChanged(nameof(PathlineGridDensity));
        OnPropertyChanged(nameof(PathlineInitialSpeed));
        OnPropertyChanged(nameof(PathlineTimeStep));
        OnPropertyChanged(nameof(PathlineMaxSteps));
        OnPropertyChanged(nameof(PathlineIsElectron));
        OnPropertyChanged(nameof(PathlineIsProton));
        OnPropertyChanged(nameof(PathlineParticleColor));
        OnPropertyChanged(nameof(VoltageIntegrationSteps));
    }
}