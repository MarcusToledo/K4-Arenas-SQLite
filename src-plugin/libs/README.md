# libs/

Dependências binárias externas que não são distribuídas via NuGet, referenciadas
diretamente pelo `K4-Arenas.csproj`.

## KitsuneMenu.dll

Necessário para compilar (`Menu`/`Menu.Enums`/`KitsuneMenu`, usados em `Plugin.cs`
e `Models/ArenaPlayerModel.cs`).

Se você já tem um servidor de CS2 rodando o plugin, copie o arquivo de lá:

```
addons/counterstrikesharp/shared/KitsuneMenu/KitsuneMenu.dll
```

para:

```
src-plugin/libs/KitsuneMenu.dll
```

Os `.dll` desta pasta não são versionados (veja `.gitignore`) — cada máquina de
build precisa da sua própria cópia.
