# OpenCube

Herramienta de mantenimiento para Windows 11, transparente y reversible. Hecha con C# y WPF.

**Idea central:** cada acción explica qué hace antes de hacerla, avisa de su gravedad y, cuando es posible, se puede revertir.

## Qué incluye

- **Panel:** CPU, RAM, disco y procesos que más memoria usan.
- **Limpieza:** temporales, volcados de fallos, cachés de navegadores y de DirectX, con vista previa de lo que se borra y lista de exclusiones.
- **Disco:** analizador de espacio por carpetas (solo lectura).
- **Programas:** lista de programas y apps de la Tienda con su desinstalador.
- **Innecesario:** detector de software que mucha gente no necesita (solo sugiere).
- **Memoria y Procesos:** reducir memoria en segundo plano y cerrar procesos auxiliares.
- **Ajustes:** ajustes de rendimiento y privacidad reversibles, con copia del valor original.
- **Inicio:** programas que arrancan con Windows.

## Requisitos

- Windows 10/11
- .NET 10 SDK para compilar (`dotnet build`)

## Estado

En desarrollo. Todavía sin licencia definida.
