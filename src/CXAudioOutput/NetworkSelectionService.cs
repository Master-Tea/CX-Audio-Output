using System.Net;
using System.Net.Sockets;

namespace CXAudioOutput;

public sealed record NetworkSelectionSnapshot(
    IReadOnlyList<NetworkAdapterSnapshot> Adapters,
    IReadOnlySet<string> SelectedIds,
    bool MultiSelect,
    int Revision,
    IReadOnlyList<LanAddress> Addresses);

public sealed class NetworkSelectionService
{
    private readonly object _sync = new();
    private readonly AppSettingsStore _settingsStore;
    private readonly ILogger<NetworkSelectionService> _logger;
    private List<NetworkAdapterSnapshot> _adapters = new();
    private HashSet<string> _selectedIds;
    private HashSet<IPAddress> _allowedAddresses = new();
    private bool _multiSelect;
    private bool _hasNetworkSetup;
    private int _revision;
    private string _signature = string.Empty;

    public NetworkSelectionService(AppSettingsStore settingsStore, ILogger<NetworkSelectionService> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        var settings = settingsStore.Load();
        _selectedIds = new HashSet<string>(settings.SelectedAdapterIds, StringComparer.OrdinalIgnoreCase);
        _multiSelect = settings.MultiSelect;
        _hasNetworkSetup = settings.HasNetworkSetup;

        Refresh();
    }

    public event EventHandler? Changed;

    public string SettingsPath => _settingsStore.FilePath;

    public bool MultiSelect
    {
        get { lock (_sync) return _multiSelect; }
    }

    public IReadOnlyList<LanAddress> GetAdvertisedAddresses()
    {
        lock (_sync) return BuildAddressesNoLock();
    }

    public NetworkSelectionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new NetworkSelectionSnapshot(
                _adapters.ToArray(),
                new HashSet<string>(_selectedIds, StringComparer.OrdinalIgnoreCase),
                _multiSelect,
                _revision,
                BuildAddressesNoLock());
        }
    }

    public bool IsSelected(string id)
    {
        lock (_sync) return _selectedIds.Contains(id);
    }

    public bool IsAllowedLocalAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (_sync) return _allowedAddresses.Contains(address);
    }

    public void Refresh()
    {
        var scanned = NetworkCatalog.Scan();
        var signature = BuildSignature(scanned);
        var changed = false;
        var shouldSave = false;

        lock (_sync)
        {
            if (!string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                _adapters = scanned.ToList();
                _signature = signature;
                changed = true;
            }

            if (!_hasNetworkSetup)
            {
                var defaults = DefaultSelection(_adapters);
                if (defaults.Count > 0)
                {
                    _selectedIds = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
                    _hasNetworkSetup = true;
                    changed = true;
                }

                shouldSave = true;
            }

            RebuildAllowedNoLock();
            if (changed)
            {
                _revision++;
            }
        }

        if (shouldSave) Save();
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool SetSelected(string id, bool selected)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var changed = false;

        lock (_sync)
        {
            if (selected)
            {
                if (!_multiSelect)
                {
                    if (_selectedIds.Count == 1 && _selectedIds.Contains(id)) return false;
                    _selectedIds.Clear();
                }

                changed = _selectedIds.Add(id);
            }
            else
            {
                changed = _selectedIds.Remove(id);
            }

            if (changed)
            {
                _hasNetworkSetup = true;
                RebuildAllowedNoLock();
                _revision++;
            }
        }

        if (changed)
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public bool SetMultiSelect(bool multiSelect)
    {
        var changed = false;
        lock (_sync)
        {
            if (_multiSelect == multiSelect) return false;
            _multiSelect = multiSelect;
            if (!multiSelect && _selectedIds.Count > 1)
            {
                var keep = _adapters.FirstOrDefault(adapter => _selectedIds.Contains(adapter.Id))?.Id
                           ?? _selectedIds.First();
                _selectedIds = new HashSet<string>(new[] { keep }, StringComparer.OrdinalIgnoreCase);
            }

            _hasNetworkSetup = true;
            RebuildAllowedNoLock();
            _revision++;
            changed = true;
        }

        if (changed)
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public bool SelectAllActive()
    {
        var ids = _adapters
            .Where(adapter => adapter.IsUp && adapter.Ipv4Addresses.Count > 0 && !adapter.IsVirtual)
            .Select(adapter => adapter.Id)
            .ToList();
        if (ids.Count == 0) return false;

        var changed = false;
        lock (_sync)
        {
            if (!_multiSelect)
            {
                _selectedIds = new HashSet<string>(new[] { ids[0] }, StringComparer.OrdinalIgnoreCase);
                changed = true;
            }
            else
            {
                foreach (var id in ids)
                {
                    if (_selectedIds.Add(id)) changed = true;
                }
            }

            if (changed)
            {
                _hasNetworkSetup = true;
                RebuildAllowedNoLock();
                _revision++;
            }
        }

        if (changed)
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public bool ClearSelection()
    {
        lock (_sync)
        {
            if (_selectedIds.Count == 0) return false;
            _selectedIds.Clear();
            _hasNetworkSetup = true;
            RebuildAllowedNoLock();
            _revision++;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RebuildAllowedNoLock()
    {
        _allowedAddresses = new HashSet<IPAddress>();
        foreach (var adapter in _adapters)
        {
            if (!_selectedIds.Contains(adapter.Id) || !adapter.IsUp) continue;
            foreach (var address in adapter.Ipv4Addresses) _allowedAddresses.Add(address);
            foreach (var address in adapter.Ipv6Addresses) _allowedAddresses.Add(address);
        }
    }

    private IReadOnlyList<LanAddress> BuildAddressesNoLock()
    {
        var addresses = new List<LanAddress>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in _adapters)
        {
            if (!_selectedIds.Contains(adapter.Id) || !adapter.IsUp) continue;
            foreach (var address in adapter.Ipv4Addresses)
            {
                var ip = address.ToString();
                if (!seen.Add(ip)) continue;
                var speedScore = (int)Math.Min(30, Math.Max(0, adapter.SpeedBitsPerSecond / 100_000_000));
                var score = (adapter.IsVirtual ? 0 : 100) + speedScore;
                addresses.Add(new LanAddress(ip, adapter.Name, score, adapter.Id));
            }
        }

        return addresses
            .OrderByDescending(address => address.Score)
            .ThenBy(address => address.Ip, StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<string> DefaultSelection(IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        var candidates = adapters
            .Where(adapter => adapter.IsUp && adapter.Ipv4Addresses.Count > 0 && !adapter.IsVirtual)
            .Select(adapter => adapter.Id)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = adapters
                .Where(adapter => adapter.IsUp && adapter.Ipv4Addresses.Count > 0)
                .Select(adapter => adapter.Id)
                .ToList();
        }

        return candidates;
    }

    private void Save()
    {
        AppSettings settings;
        lock (_sync)
        {
            settings = new AppSettings
            {
                SelectedAdapterIds = _selectedIds.ToList(),
                MultiSelect = _multiSelect,
                HasNetworkSetup = _hasNetworkSetup
            };
        }

        if (!_settingsStore.TrySave(settings))
        {
            _logger.LogWarning("Could not save network selection to {Path}", _settingsStore.FilePath);
        }
    }

    private static string BuildSignature(IEnumerable<NetworkAdapterSnapshot> adapters)
    {
        return string.Join("|", adapters.Select(adapter =>
            string.Join("~",
                adapter.Id,
                adapter.IsUp,
                adapter.StatusText,
                adapter.SpeedBitsPerSecond,
                adapter.Name,
                adapter.Description,
                adapter.TypeLabel,
                adapter.IsVirtual,
                string.Join(",", adapter.Ipv4Addresses.Select(address => address.ToString())),
                string.Join(",", adapter.Ipv6Addresses.Select(address => address.ToString())))));
    }
}
