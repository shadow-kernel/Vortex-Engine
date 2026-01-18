using System.ComponentModel;
using System.Runtime.Serialization;

namespace Editor.Core
{
    /// <summary>
    /// Basisklasse für alle ViewModels mit INotifyPropertyChanged Support.
    /// Verwendet DataContract für binäre und JSON Serialisierung.
    /// </summary>
    [DataContract]
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, string propertyName)
        {
            if (Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
