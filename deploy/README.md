# Déploiement

Deux scénarios selon qui a une machine Windows avec le SDK.

| | Scénario A — **tu** compiles | Scénario B — **elle** compile |
|---|---|---|
| Quand | tu as accès à un Windows + .NET 8 SDK | tu es sur Linux / sans Windows |
| Elle reçoit | un zip clé-en-main (aucun build) | le **code source** (zip ou repo) |
| Elle lance | `Lancer-StutterDiag.bat` → `1` | `Build-Windows.bat`, puis boucle d'erreurs |
| Fichiers pour elle | `LISEZ-MOI.txt` | `LISEZ-MOI-COMPILATION.txt` |

Le code **n'a jamais été compilé** : la première compilation échoue presque
toujours. Voir [`../docs/INTEGRATION-NOTES.md`](../docs/INTEGRATION-NOTES.md).

---

## Scénario B — elle compile (ton cas)

### Ce que tu lui envoies
Le dépôt complet. Soit :
- un **zip du code source** (`git archive --format=zip -o src.zip HEAD`, ou l'export
  GitHub → *Code ▸ Download ZIP* si le repo est rendu public), extrait chez elle ;
- soit tu la fais `gh auth login` / `git clone` (repo privé → il lui faut un accès).

Le dossier `deploy/` est déjà dedans.

### Ce qu'elle fait
1. Double-clic sur **`deploy/Build-Windows.bat`**.
2. Le script :
   - installe le **SDK .NET 8 dans son profil** (sans droits admin) s'il manque ;
   - fait `git pull` si c'est un dépôt ;
   - `dotnet build StutterDiag.sln -c Release`, sortie tee-ée dans `build-log.txt`.
3. **Échec** → il écrit `deploy/build-errors.txt` (liste courte des `error CSxxxx`)
   + `deploy/build-log.txt`, ouvre le premier dans le Bloc-notes, et s'arrête.
   → elle t'envoie **`build-errors.txt`** (petit, colle-le moi tel quel).
4. Toi → tu corriges, `git commit && git push`.
5. Elle relance `Build-Windows.bat` (il refait `git pull` tout seul). Boucle
   jusqu'à **succès** — compte 2 à 4 tours.
6. **Succès** → il fabrique le pack (`artifacts/StutterDiag-Setup-*.zip`) **et**
   installe le service sur ce PC (une invite UAC, `-Wait` : le script attend la fin).

Ensuite, gestion courante via `deploy/Lancer-StutterDiag.bat` (menu FR ci-dessous).

### Piloter à distance sans le menu (lignes uniques à lui dicter)
```powershell
powershell -ExecutionPolicy Bypass -File deploy\Build-Windows.ps1            # compiler + installer
powershell -ExecutionPolicy Bypass -File deploy\Build-Windows.ps1 -NoInstall # compiler seulement
powershell -ExecutionPolicy Bypass -File deploy\StutterDiag.ps1 -Action status
powershell -ExecutionPolicy Bypass -File deploy\StutterDiag.ps1 -Action report
powershell -ExecutionPolicy Bypass -File deploy\StutterDiag.ps1 -Action uninstall -RemoveData
```

---

## Scénario A — tu compiles, elle installe seulement

Sur ta machine Windows + .NET 8 SDK, une fois que ça compile :

```powershell
dotnet build StutterDiag.sln -c Release
.\deploy\publish-release.ps1            # -> artifacts\StutterDiag-Setup-<version>.zip
.\deploy\publish-release.ps1 -Release   # (option) attache le zip a la release GitHub
```

Le zip contient l'appli (build portable *self-contained*, **aucun runtime** à
installer chez elle) + les scripts + `LISEZ-MOI.txt`. Tu lui envoies ce seul zip.

Elle : clic droit → *Extraire tout* → double-clic **`Lancer-StutterDiag.bat`** →
*Oui* → taper **`1`**. Terminé.

---

## Le menu (`Lancer-StutterDiag.bat`)

```
1  Installer / mettre a jour et demarrer
2  Voir l'etat
3  Demarrer la surveillance
4  Arreter la surveillance
5  Generer un rapport HTML (sur le Bureau)  <- a t'envoyer
6  Ouvrir l'interface
7  Desinstaller le service
0  Quitter
```

Le service tourne en arrière-plan : il survit à la fermeture de l'interface et
aux redémarrages. `StutterDiag.ps1` accepte les mêmes actions en direct :
`-Action install|update|start|stop|status|report|gui|uninstall`
(`-InstallDir`, `-PackageZip`, `-PackageUrl`, `-RemoveData` en options).

---

## Fichiers de ce dossier

| Fichier | Rôle | Pour qui |
|---|---|---|
| `Build-Windows.bat` / `.ps1` | installe le SDK, compile, fabrique le pack, installe le service | opérateur (scénario B) |
| `Lancer-StutterDiag.bat` | menu FR de gestion (install/rapport/etc.) | opérateur |
| `StutterDiag.ps1` | toute la logique de gestion (auto-élévation) | — |
| `publish-release.ps1` | build portable → pack → release GitHub | toi (scénario A) |
| `LISEZ-MOI.txt` | 3 étapes, pack pré-compilé | opérateur (scénario A) |
| `LISEZ-MOI-COMPILATION.txt` | étapes + boucle d'erreurs | opérateur (scénario B) |

---

## Notes

- **Admin :** demandé **une seule fois**, pour enregistrer le service. La compilation
  et l'installation du SDK se font sans admin (profil utilisateur). L'interface se
  lance sans élévation (`explorer.exe`).
- **Sans admin du tout :** l'appli tourne quand même mais sans la session ETW noyau
  (pas de latence DPC/ISR ni d'I/O disque par requête) ; l'interface le signale.
- **Données :** local uniquement, `C:\ProgramData\StutterDiag`. Aucune télémétrie.
- **Journaux :** `deploy\build-log.txt` (compilation), `C:\StutterDiag\deploy.log`
  (installation/gestion).
