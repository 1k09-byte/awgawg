using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using stellarisKIT.Services;
using System;
using System.Collections.ObjectModel;

namespace stellarisKIT.ViewModels
{
    public partial class ReservedCpuSetsViewModel : ObservableObject
    {
        private readonly ReservedCpuSetsService _service = new();

        public ObservableCollection<CpuCoreItem> Cores { get; } = new();

        [ObservableProperty] private bool _isElevated;
        public bool IsNotElevated => !IsElevated;
        
        [ObservableProperty] private bool _requiresPerBootReapply;
        [ObservableProperty] private bool _applyAtStartup;
        
        [ObservableProperty] private bool _hasError;
        [ObservableProperty] private string _errorText = string.Empty;

        [ObservableProperty] private bool _hasSuccess;
        [ObservableProperty] private string _successText = string.Empty;

        public ReservedCpuSetsViewModel()
        {
            IsElevated = _service.IsElevated();
            RequiresPerBootReapply = _service.RequiresPerBootReapply();
            ApplyAtStartup = _service.GetApplyAtStartup();
            
            LoadCores();
        }

        private void LoadCores()
        {
            Cores.Clear();
            int processorCount = Environment.ProcessorCount;
            ulong? mask = _service.GetReservedCpuMask();
            ulong actualMask = mask ?? 0;

            for (int i = 0; i < processorCount; i++)
            {
                bool isReserved = (actualMask & (1UL << i)) != 0;
                Cores.Add(new CpuCoreItem(i, isReserved));
            }
        }

        partial void OnApplyAtStartupChanged(bool value)
        {
            if (IsElevated && RequiresPerBootReapply)
            {
                try
                {
                    _service.SetApplyAtStartup(value);
                }
                catch (Exception ex)
                {
                    ShowError($"Failed to set startup key: {ex.Message}");
                }
            }
        }

        [RelayCommand]
        private void SelectAll()
        {
            foreach (var core in Cores)
            {
                core.IsReserved = true;
            }
        }

        [RelayCommand]
        private void SelectNone()
        {
            foreach (var core in Cores)
            {
                core.IsReserved = false;
            }
        }

        [RelayCommand]
        private void InvertSelection()
        {
            foreach (var core in Cores)
            {
                core.IsReserved = !core.IsReserved;
            }
        }

        [RelayCommand]
        private void Save()
        {
            HasError = false;
            HasSuccess = false;

            if (!IsElevated)
            {
                ShowError("Administrator permissions are required to save these changes. Please restart the application as Administrator.");
                return;
            }

            ulong newMask = 0;
            int reservedCount = 0;

            foreach (var core in Cores)
            {
                if (core.IsReserved)
                {
                    newMask |= (1UL << core.Index);
                    reservedCount++;
                }
            }

            if (reservedCount == Cores.Count)
            {
                ShowError("You cannot reserve all logical processors. At least one core must remain unreserved for the kernel scheduler to function.");
                return;
            }

            try
            {
                _service.SetReservedCpuMask(newMask);
                ShowSuccess("CPU reservations saved successfully. You must restart your computer for these changes to take effect.");
            }
            catch (Exception ex)
            {
                ShowError($"Failed to save reserved CPU sets: {ex.Message}");
            }
        }

        private void ShowError(string message)
        {
            HasError = true;
            ErrorText = message;
            HasSuccess = false;
        }

        private void ShowSuccess(string message)
        {
            HasSuccess = true;
            SuccessText = message;
            HasError = false;
        }
    }

    public partial class CpuCoreItem : ObservableObject
    {
        public int Index { get; }
        public string Name => $"Core {Index}";

        [ObservableProperty] private bool _isReserved;

        public CpuCoreItem(int index, bool isReserved)
        {
            Index = index;
            IsReserved = isReserved;
        }
    }
}
