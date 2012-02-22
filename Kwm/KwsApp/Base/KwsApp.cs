using kwmlib;
using System;

namespace kwm
{
    /// <summary>
    /// This class represents an application in a workspace.
    /// </summary>
    public abstract class KwsApp
    {
        /// <summary>
        /// Reference to the workspace.
        /// </summary>
        public Workspace Kws;

        /// <summary>
        /// Status of the application.
        /// </summary>
        public KwsAppStatus AppStatus = KwsAppStatus.Stopped;

        /// <summary>
        /// ID of the application.
        /// </summary>
        public abstract UInt32 AppID { get; }

        public virtual void Relink(Workspace kws)
        {
            Kws = kws;
            Kws.AppTree[AppID] = this;
        }

        /// <summary>
        /// Prepare the application to work online or offline. This method
        /// throws on error.
        /// </summary>
        public virtual void PrepareToWork()
        {
        }

        /// <summary>
        /// Prepare the application to be rebuilt. Reset all the data related
        /// to the application. This includes data in files, in the database
        /// and in RAM. This method throws on error.
        /// </summary>
        public virtual void PrepareToRebuild()
        {
        }

        /// <summary>
        /// Prepare the application to be removed. Delete all the data related 
        /// to the application. This includes data in files, in the database 
        /// and in RAM. This method must not throw.
        /// </summary>
        public virtual void PrepareToRemove()
        {
        }

        /// <summary>
        /// Handle an ANP event associated to this application.
        /// </summary>
        public virtual KwsAnpEventStatus HandleAnpEvent(AnpMsg evt)
        {
            return KwsAnpEventStatus.Unprocessed;
        }

        /// <summary>
        /// Start the application. When the application has started, call 
        /// OnAppStarted().
        /// </summary>
        public virtual void Start()
        {
            OnAppStarted();
        }

        /// <summary>
        /// Stop the application. When the application is stopped, call 
        /// OnAppStopped(). Ex is non-null if the application is stopping
        /// because an error occurred.
        /// </summary>
        public virtual void Stop(Exception ex)
        {
            OnAppStopped();
        }

        /// <summary>
        /// This method must be called by the application when it has started.
        /// </summary>
        public void OnAppStarted()
        {
            if (AppStatus != KwsAppStatus.Starting) return;
            AppStatus = KwsAppStatus.Started;
            Kws.OnKwsSmNotif += OnKwsSmNotif;
            Kws.Sm.OnAppStarted();
        }

        /// <summary>
        /// This method must be called by the application when it has stopped.
        /// </summary>
        public void OnAppStopped()
        {
            if (AppStatus != KwsAppStatus.Stopping) return;
            AppStatus = KwsAppStatus.Stopped;
            Kws.OnKwsSmNotif -= OnKwsSmNotif;
            Kws.Sm.OnAppStopped();
        }

        /// <summary>
        /// This method is called when a workspace notification is received.
        /// </summary>
        public virtual void OnKwsSmNotif(Object sender, KwsSmNotif n)
        {
        }

        /// <summary>
        /// This method must be called when the application fails.
        /// </summary>
        public void OnAppFailure(Exception ex)
        {
            Kws.Sm.HandleMiscFailure(ex);
        }

        /// <summary>
        /// Return true if the workspace has a level of functionality greater
        /// or equal to the offline mode.
        /// </summary>
        public bool IsOfflineCapable()
        {
            return Kws.IsOfflineCapable();
        }

        /// <summary>
        /// Return true if the workspace has a level of functionality equal to
        /// the online mode.
        /// </summary>
        public bool IsOnlineCapable()
        {
            return Kws.IsOnlineCapable();
        }

        /// <summary>
        /// Post a KCD command.
        /// </summary>
        public KcdQuery PostKcdCmd(AnpMsg cmd, KcdQueryDelegate callback)
        {
            return Kws.PostKcdCmd(cmd, callback);
        }
    }
}