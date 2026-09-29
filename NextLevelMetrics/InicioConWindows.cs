using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace NextLevelMetrics;

internal static class InicioConWindows
{
    internal const string NombreTarea = "Next Level Metrics - Inicio automático";
    private const string Identificador = "NextLevelMetrics.InicioAutomatico.v1";
    private const int CrearOActualizar = 6;
    private const int InicioSesionInteractiva = 3;
    private const int PrivilegiosMaximos = 1;
    private const int DisparadorInicioSesion = 9;
    private const int AccionEjecutar = 0;
    private const int IgnorarNuevaInstancia = 2;

    internal static bool EstaActivo()
    {
        try
        {
            dynamic carpeta = AbrirCarpeta();
            dynamic? tarea = BuscarTarea(carpeta);
            return tarea is not null && EsNuestra(tarea) && EsConfiguracionActual(tarea);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"No se pudo consultar el inicio con Windows: {ex}");
            throw new InvalidOperationException("No se pudo consultar el inicio con Windows.", ex);
        }
    }

    internal static void Establecer(bool activar)
    {
        try
        {
            dynamic carpeta = AbrirCarpeta();
            dynamic? existente = BuscarTarea(carpeta);
            if (existente is not null && !EsNuestra(existente))
                throw new InvalidOperationException(
                    "Ya existe una tarea con ese nombre que no pertenece a Next Level Metrics. No se ha modificado.");

            if (!activar)
            {
                if (existente is not null) carpeta.DeleteTask(NombreTarea, 0);
                return;
            }

            string ejecutable = RutaEjecutable();
            if (!File.Exists(ejecutable))
                throw new InvalidOperationException("No se encontró el ejecutable actual de Next Level Metrics.");

            dynamic servicio = ActivarServicio();
            dynamic definicion = servicio.NewTask(0);
            definicion.RegistrationInfo.Source = Identificador;
            definicion.RegistrationInfo.Description =
                "Inicia Next Level Metrics al iniciar sesión en Windows.";
            definicion.Principal.UserId = UsuarioActual();
            definicion.Principal.LogonType = InicioSesionInteractiva;
            definicion.Principal.RunLevel = PrivilegiosMaximos;
            definicion.Settings.Enabled = true;
            definicion.Settings.Hidden = false;
            definicion.Settings.StartWhenAvailable = true;
            definicion.Settings.DisallowStartIfOnBatteries = false;
            definicion.Settings.StopIfGoingOnBatteries = false;
            definicion.Settings.ExecutionTimeLimit = "PT0S";
            definicion.Settings.MultipleInstances = IgnorarNuevaInstancia;

            dynamic disparador = definicion.Triggers.Create(DisparadorInicioSesion);
            disparador.UserId = UsuarioActual();
            disparador.Enabled = true;

            dynamic accion = definicion.Actions.Create(AccionEjecutar);
            accion.Path = ejecutable;
            accion.WorkingDirectory = Path.GetDirectoryName(ejecutable)!;

            carpeta.RegisterTaskDefinition(NombreTarea, definicion, CrearOActualizar,
                null, null, InicioSesionInteractiva, null);

            dynamic? registrada = BuscarTarea(carpeta);
            if (registrada is null || !EsNuestra(registrada) || !EsConfiguracionActual(registrada))
                throw new InvalidOperationException(
                    "Windows no confirmó la configuración del inicio automático.");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            Trace.TraceWarning($"No se pudo cambiar el inicio con Windows: {ex}");
            throw new InvalidOperationException(
                "No se pudo cambiar el inicio con Windows. Comprueba los permisos de la aplicación.", ex);
        }
    }

    private static dynamic AbrirCarpeta()
    {
        dynamic servicio = ActivarServicio();
        return servicio.GetFolder("\\");
    }

    private static dynamic ActivarServicio()
    {
        Type tipo = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("El Programador de tareas de Windows no está disponible.");
        dynamic servicio = Activator.CreateInstance(tipo)!;
        servicio.Connect();
        return servicio;
    }

    private static dynamic? BuscarTarea(dynamic carpeta)
    {
        dynamic tareas = carpeta.GetTasks(1);
        for (int i = 1; i <= tareas.Count; i++)
        {
            dynamic tarea = tareas.Item(i);
            if (string.Equals((string)tarea.Name, NombreTarea, StringComparison.OrdinalIgnoreCase))
                return tarea;
        }
        return null;
    }

    private static bool EsNuestra(dynamic tarea)
    {
        dynamic definicion = tarea.Definition;
        return string.Equals((string)definicion.RegistrationInfo.Source,
                   Identificador, StringComparison.Ordinal) &&
               EsUsuarioActual((string)definicion.Principal.UserId);
    }

    private static bool EsConfiguracionActual(dynamic tarea)
    {
        dynamic definicion = tarea.Definition;
        if (!(bool)tarea.Enabled ||
            (int)definicion.Principal.RunLevel != PrivilegiosMaximos ||
            (int)definicion.Principal.LogonType != InicioSesionInteractiva ||
            (int)definicion.Settings.MultipleInstances != IgnorarNuevaInstancia ||
            (int)definicion.Actions.Count != 1 ||
            (int)definicion.Triggers.Count != 1)
            return false;

        dynamic accion = definicion.Actions.Item(1);
        dynamic disparador = definicion.Triggers.Item(1);
        return (int)accion.Type == AccionEjecutar &&
               string.Equals(Path.GetFullPath((string)accion.Path), RutaEjecutable(),
                   StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty((string)accion.Arguments) &&
               (int)disparador.Type == DisparadorInicioSesion &&
               (bool)disparador.Enabled &&
               EsUsuarioActual((string)disparador.UserId);
    }

    private static bool EsUsuarioActual(string usuario) =>
        string.Equals(usuario, UsuarioActual(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(usuario, WindowsIdentity.GetCurrent().Name,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(usuario, WindowsIdentity.GetCurrent().Name.Split('\\').Last(),
            StringComparison.OrdinalIgnoreCase);

    private static string UsuarioActual() => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("No se pudo identificar al usuario actual de Windows.");

    private static string RutaEjecutable() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "NextLevelMetrics.exe"));
}
