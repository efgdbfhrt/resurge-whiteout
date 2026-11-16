const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { spawn } = require('child_process');
const https = require('https');
let WebTorrent;
let webtorrentClient = null;
let currentTorrent = null;

app.disableHardwareAcceleration();
app.commandLine.appendSwitch('disable-gpu');

let mainWindow;
let gameInstallPath = '';

// Load saved settings
const settingsPath = path.join(os.homedir(), 'boiii-uwp-settings.json');

function loadSettings() {
  try {
    if (fs.existsSync(settingsPath)) {
      const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
      gameInstallPath = settings.gameInstallPath || '';
    }
  } catch (error) {
    console.error('Error loading settings:', error);
  }
}

function saveSettings() {
  try {
    const settings = { gameInstallPath };
    fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2));
  } catch (error) {
    console.error('Error saving settings:', error);
  }
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1100,
    height: 950,
    frame: false,
    backgroundColor: '#121212',
    webPreferences: {
      nodeIntegration: true,
      contextIsolation: false
    },
    icon: path.join(__dirname, 'assets', 'icon.png'),
    resizable: true,
    autoHideMenuBar: true,
    minWidth: 900,
    minHeight: 750
  });

  mainWindow.loadFile('index.html');

  // Send current game path to renderer when window is ready
  mainWindow.webContents.once('did-finish-load', () => {
    mainWindow.webContents.send('game-path-loaded', gameInstallPath);
  });
}

app.whenReady().then(() => {
  loadSettings();
  createWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

// IPC handlers
ipcMain.handle('select-game-folder', async () => {
  const result = await dialog.showOpenDialog(mainWindow, {
    properties: ['openDirectory'],
    title: 'Select Game Folder (containing blackops3.exe)'
  });

  if (!result.canceled && result.filePaths.length > 0) {
    const selectedPath = result.filePaths[0];
    const blackops3Path = path.join(selectedPath, 'blackops3.exe');

    if (fs.existsSync(blackops3Path)) {
      gameInstallPath = selectedPath;
      saveSettings();
      return { success: true, path: selectedPath };
    } else {
      return { success: false, error: 'blackops3.exe not found in selected folder' };
    }
  }

  return { success: false, error: 'No folder selected' };
});

ipcMain.handle('install-mod', async () => {
  if (!gameInstallPath) {
    return { success: false, error: 'Please select game folder first' };
  }

  try {
    const originalExe = path.join(gameInstallPath, 'blackops3.exe');
    const backupExe = path.join(gameInstallPath, 'blackops3_untouched.exe');

    // Backup original exe if backup doesn't exist
    if (!fs.existsSync(backupExe) && fs.existsSync(originalExe)) {
      fs.copyFileSync(originalExe, backupExe);
    }

    // Check for both production structure (boiii assets/) and dev structure (root folders)
    const appPath = process.resourcesPath || __dirname;

    const prodSourceBoiiiPath = path.join(appPath, 'boiii assets', 'boiii');
    const devSourceBoiiiPath = path.join(__dirname, 'boiii');

    let finalBoiiiPath = fs.existsSync(prodSourceBoiiiPath) ? prodSourceBoiiiPath : devSourceBoiiiPath;

    let hasErrors = [];
    if (!fs.existsSync(finalBoiiiPath)) hasErrors.push('boiii');

    if (hasErrors.length > 0) {
      return { success: false, error: `Missing folders: ${hasErrors.join(', ')}` };
    }

    await copyDirectoryContents(finalBoiiiPath, gameInstallPath);

    // Copy ms folder contents to game directory
    const prodMsSourcePath = path.join(appPath, 'boiii assets', 'ms');
    const devMsSourcePath = path.join(__dirname, 'ms');
    let finalMsPath = fs.existsSync(prodMsSourcePath) ? prodMsSourcePath : devMsSourcePath;

    if (!fs.existsSync(finalMsPath)) {
      return { success: false, error: 'Missing folder: ms' };
    }
    await copyDirectoryContents(finalMsPath, gameInstallPath);
    await setupGscFolder();

    return { success: true };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

ipcMain.handle('clean-wgs-cache-folder', async () => {
  try {
    const wgsCachePath = path.join(
      os.homedir(),
      'AppData',
      'Local',
      'Packages',
      '38985CA0.CallofDutyBlackOps3PCMS_5bkah9njm3e9g'
    );

    if (!fs.existsSync(wgsCachePath)) {
      return { success: true, message: 'WGS cache folder not found' };
    }

    await fs.promises.rm(wgsCachePath, { recursive: true, force: true });

    return { success: true, message: 'WGS cache folder cleaned' };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

function downloadFile(url, dest) {
  return new Promise((resolve, reject) => {
    https.get(url, (response) => {
      if (response.statusCode === 301 || response.statusCode === 302 || response.statusCode === 307) {
        return downloadFile(response.headers.location, dest).then(resolve).catch(reject);
      }
      if (response.statusCode !== 200) {
        return reject(new Error(`Failed to download, status code: ${response.statusCode}`));
      }
      
      const file = fs.createWriteStream(dest);
      response.pipe(file);
      
      file.on('finish', () => {
        file.close(() => resolve());
      });
      
      file.on('error', (err) => {
        file.close();
        fs.unlink(dest, () => reject(err));
      });
    }).on('error', (err) => {
      reject(err);
    });
  });
}

ipcMain.handle('repair-gaming-services', async () => {
  try {
    const tempDir = os.tmpdir();
    // Use a unique filename to avoid EBUSY if the tool is already running
    const repairToolPath = path.join(tempDir, `GamingRepairTool_${Date.now()}.exe`);
    
    // Download the tool directly
    await downloadFile('https://aka.ms/GamingRepairTool', repairToolPath);
    
    // Launch it
    spawn(repairToolPath, [], {
      detached: true,
      stdio: 'ignore'
    }).unref();
    
    return { success: true };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

ipcMain.handle('install-resurge-only', async () => {
  if (!gameInstallPath) {
    return { success: false, error: 'Please select game folder first' };
  }

  try {
    const originalExe = path.join(gameInstallPath, 'blackops3.exe');
    const backupExe = path.join(gameInstallPath, 'blackops3_untouched.exe');

    // Backup original exe if backup doesn't exist
    if (!fs.existsSync(backupExe) && fs.existsSync(originalExe)) {
      fs.copyFileSync(originalExe, backupExe);
    }

    const appPath = process.resourcesPath || __dirname;

    // Copy ms folder contents to game directory
    const prodMsSourcePath = path.join(appPath, 'boiii assets', 'ms');
    const devMsSourcePath = path.join(__dirname, 'ms');
    let finalMsPath = fs.existsSync(prodMsSourcePath) ? prodMsSourcePath : devMsSourcePath;

    if (!fs.existsSync(finalMsPath)) {
      return { success: false, error: 'Missing folder: ms' };
    }
    await copyDirectoryContents(finalMsPath, gameInstallPath);
    await setupGscFolder();

    return { success: true };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

ipcMain.handle('import-steam-workshop-mods-maps', async () => {
  if (!gameInstallPath) {
    return { success: false, error: 'Please select game folder first' };
  }

  try {
    const workshopRoots = findSteamWorkshopRoots();

    if (workshopRoots.length === 0) {
      return { success: false, error: 'Steam workshop content folder not found' };
    }

    const importedItems = [];

    for (const rootPath of workshopRoots) {
      const workshopEntries = await fs.promises.readdir(rootPath, { withFileTypes: true });

      for (const entry of workshopEntries) {
        if (!entry.isDirectory() || !/^\d+$/.test(entry.name)) {
          continue;
        }

        const sourceFolderPath = path.join(rootPath, entry.name);
        const workshopJsonPath = resolveWorkshopJsonPath(sourceFolderPath);

        if (!workshopJsonPath) {
          continue;
        }

        const workshopData = JSON.parse(await fs.promises.readFile(workshopJsonPath, 'utf8'));
        const workshopType = String(workshopData.Type || '').trim().toLowerCase();

        if (workshopType !== 'mod' && workshopType !== 'map') {
          continue;
        }

        const destinationRoot = path.join(gameInstallPath, workshopType === 'mod' ? 'mods' : 'usermaps');
        await fs.promises.mkdir(destinationRoot, { recursive: true });

        const destinationFolderPath = path.join(destinationRoot, entry.name);

        if (fs.existsSync(destinationFolderPath)) {
          await fs.promises.rm(destinationFolderPath, { recursive: true, force: true });
        }

        await copyDirectory(sourceFolderPath, destinationFolderPath);
        await fixWorkshopFolder(destinationFolderPath, destinationRoot);

        importedItems.push({
          sourceId: entry.name,
          type: workshopType,
          title: workshopData.Title || entry.name
        });
      }
    }

    if (importedItems.length === 0) {
      return { success: true, message: 'No Steam Workshop mods/maps were imported' };
    }

    return {
      success: true,
      message: `Imported ${importedItems.length} Steam Workshop item(s)`
    };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

ipcMain.handle('get-gsc-list', async () => {
  try {
    if (!gameInstallPath) {
      return { success: false, error: 'Please select game folder first' };
    }

    await setupGscFolder();
    const destGscDir = path.join(gameInstallPath, 'gsc');

    const files = await fs.promises.readdir(destGscDir);
    const gscFiles = files.filter(f => f.toLowerCase().endsWith('.gsc') || f.toLowerCase().endsWith('.gscc'));
    return { success: true, files: gscFiles, dir: destGscDir };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

ipcMain.handle('inject-gsc', async (event, fileName) => {
  try {
    if (!gameInstallPath) {
      return { success: false, error: 'Please select game folder first' };
    }

    const injector = require('./injector');
    const gscPath = path.join(gameInstallPath, 'gsc', fileName);
    if (!fs.existsSync(gscPath)) {
      return { success: false, error: 'GSC file not found in game folder' };
    }
    
    // Attempt injection using native APIs in node
    const result = await injector.injectGsc(gscPath);
    return { success: true, message: result };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

ipcMain.handle('show-game-folder', async () => {
  if (gameInstallPath) {
    shell.openPath(gameInstallPath);
  }
});

ipcMain.handle('run-executable', async (event, exeName) => {
  if (!gameInstallPath) {
    return { success: false, error: 'Please select game folder first' };
  }

  try {
    const exePath = path.join(gameInstallPath, exeName);

    if (!fs.existsSync(exePath)) {
      return { success: false, error: `${exeName} not found in game folder` };
    }

    // Launch the executable
    spawn(exePath, [], {
      detached: true,
      stdio: 'ignore',
      cwd: gameInstallPath
    }).unref();

    return { success: true };
  } catch (error) {
    return { success: false, error: error.message };
  }
});

ipcMain.handle('open-url', async (event, url) => {
  shell.openExternal(url);
});

ipcMain.handle('window-minimize', async () => {
  if (mainWindow) {
    mainWindow.minimize();
  }
});

ipcMain.handle('window-maximize-toggle', async () => {
  if (mainWindow) {
    if (mainWindow.isMaximized()) {
      mainWindow.unmaximize();
    } else {
      mainWindow.maximize();
    }
  }
});

ipcMain.handle('window-close', async () => {
  if (mainWindow) {
    mainWindow.close();
  }
});

// Helper functions
async function copyDirectory(src, dest) {
  await fs.promises.mkdir(dest, { recursive: true });
  const entries = await fs.promises.readdir(src, { withFileTypes: true });

  for (const entry of entries) {
    const srcPath = path.join(src, entry.name);
    const destPath = path.join(dest, entry.name);

    if (entry.isDirectory()) {
      await copyDirectory(srcPath, destPath);
    } else {
      await fs.promises.copyFile(srcPath, destPath);
    }
  }
}

async function copyDirectoryContents(src, dest) {
  const entries = await fs.promises.readdir(src, { withFileTypes: true });

  for (const entry of entries) {
    const srcPath = path.join(src, entry.name);
    const destPath = path.join(dest, entry.name);

    if (entry.isDirectory()) {
      await copyDirectory(srcPath, destPath);
    } else {
      await fs.promises.copyFile(srcPath, destPath);
    }
  }
}

function findSteamWorkshopRoots() {
  const roots = [];
  const driveLetters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');
  const candidateSuffixes = [
    path.join('Program Files (x86)', 'Steam', 'steamapps', 'workshop', 'content', '311210'),
    path.join('Program Files', 'Steam', 'steamapps', 'workshop', 'content', '311210'),
    path.join('Steam', 'steamapps', 'workshop', 'content', '311210'),
    path.join('SteamLibrary', 'steamapps', 'workshop', 'content', '311210')
  ];

  for (const driveLetter of driveLetters) {
    const driveRoot = `${driveLetter}:\\`;

    if (!fs.existsSync(driveRoot)) {
      continue;
    }

    for (const suffix of candidateSuffixes) {
      const candidatePath = path.join(driveRoot, suffix);
      if (fs.existsSync(candidatePath)) {
        roots.push(candidatePath);
      }
    }
  }

  return [...new Set(roots)];
}

function resolveWorkshopJsonPath(folderPath) {
  const directPath = path.join(folderPath, 'workshop.json');
  if (fs.existsSync(directPath)) {
    return directPath;
  }

  const zonePath = path.join(folderPath, 'zone', 'workshop.json');
  if (fs.existsSync(zonePath)) {
    return zonePath;
  }

  return '';
}

async function fixWorkshopFolder(folderPath, destinationRoot) {
  const workshopJsonPath = resolveWorkshopJsonPath(folderPath);
  if (!workshopJsonPath) {
    return;
  }

  const workshopData = JSON.parse(await fs.promises.readFile(workshopJsonPath, 'utf8'));
  const targetFolderName = typeof workshopData.FolderName === 'string' ? workshopData.FolderName.trim() : '';

  let workingFolderPath = folderPath;
  if (targetFolderName && /^\d+$/.test(path.basename(folderPath)) && path.basename(folderPath) !== targetFolderName) {
    const renamedFolderPath = path.join(destinationRoot, targetFolderName);

    if (fs.existsSync(renamedFolderPath)) {
      await fs.promises.rm(renamedFolderPath, { recursive: true, force: true });
    }

    await fs.promises.rename(folderPath, renamedFolderPath);
    workingFolderPath = renamedFolderPath;
  }

  const zonePath = path.join(workingFolderPath, 'zone');
  if (!fs.existsSync(zonePath)) {
    const looseFiles = await fs.promises.readdir(workingFolderPath, { withFileTypes: true });
    const looseFilePaths = looseFiles
      .filter(entry => entry.isFile())
      .map(entry => path.join(workingFolderPath, entry.name));

    if (looseFilePaths.length > 0) {
      await fs.promises.mkdir(zonePath, { recursive: true });

      for (const sourcePath of looseFilePaths) {
        const destinationPath = path.join(zonePath, path.basename(sourcePath));
        await movePathAsync(sourcePath, destinationPath);
      }
    }
  }
}

async function movePathAsync(sourcePath, destinationPath) {
  try {
    await fs.promises.rename(sourcePath, destinationPath);
  } catch (error) {
    await fs.promises.copyFile(sourcePath, destinationPath);
    await fs.promises.unlink(sourcePath);
  }
}

async function setupGscFolder() {
  if (!gameInstallPath) return;
  const destGscDir = path.join(gameInstallPath, 'gsc');
  const appPath = process.resourcesPath || __dirname;
  
  // Ensure we can locate the bundled gsc folder
  const prodGscDir = path.join(appPath, 'boiii assets', 'gsc');
  const devGscDir = path.join(__dirname, 'gsc');
  let sourceGscDir = fs.existsSync(prodGscDir) ? prodGscDir : devGscDir;

  if (!fs.existsSync(destGscDir)) {
    await fs.promises.mkdir(destGscDir, { recursive: true });
    if (fs.existsSync(sourceGscDir)) {
      await copyDirectoryContents(sourceGscDir, destGscDir);
    }
  }
}

// --- WEBTORRENT LOGIC ---

function formatBytes(bytes, decimals = 2) {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const dm = decimals < 0 ? 0 : decimals;
  const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(dm)) + ' ' + sizes[i];
}

function formatTime(ms) {
  if (ms === Infinity || isNaN(ms)) return 'Calculating...';
  const seconds = Math.floor(ms / 1000);
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${m}m ${s}s remaining`;
}

ipcMain.handle('start-webtorrent-download', async (event, type) => {
  if (!WebTorrent) {
    try { 
      const module = await import('webtorrent');
      WebTorrent = module.default || module;
    } catch(e) { 
      return { success: false, error: 'WebTorrent module not found: ' + e.message }; 
    }
  }
  
  if (!webtorrentClient) {
    webtorrentClient = new WebTorrent();
  }

  const result = await dialog.showOpenDialog(mainWindow, {
    properties: ['openDirectory'],
    title: 'Select Destination Folder'
  });

  if (result.canceled || result.filePaths.length === 0) {
    return { success: false, error: 'Download canceled by user.' };
  }

  const destPath = result.filePaths[0];

  const appPath = process.resourcesPath || __dirname;
  const prodDowngradeDir = path.join(appPath, 'boiii assets', 'downgrade');
  const devDowngradeDir = path.join(__dirname, 'downgrade');
  let downgradeDir = fs.existsSync(prodDowngradeDir) ? prodDowngradeDir : devDowngradeDir;

  const torrentFileName = type === 'unpacked' ? 'OPTION 1 UNPACKED.torrent' : 'OPTION 2 PACKED.torrent';
  const torrentFile = path.join(downgradeDir, torrentFileName);

  if (!fs.existsSync(torrentFile)) {
    return { success: false, error: 'Torrent file not found in downgrade folder.' };
  }

  if (currentTorrent) {
    currentTorrent.destroy();
    currentTorrent = null;
  }

  currentTorrent = webtorrentClient.add(torrentFile, { path: destPath }, (torrent) => {
    
    let interval = setInterval(() => {
      if (!currentTorrent) {
        clearInterval(interval);
        return;
      }
      event.sender.send('webtorrent-progress', {
        percentage: currentTorrent.progress * 100,
        speed: formatBytes(currentTorrent.downloadSpeed) + '/s',
        timeRemaining: formatTime(currentTorrent.timeRemaining),
        paused: currentTorrent.paused,
        numPeers: currentTorrent.numPeers,
        downloaded: formatBytes(currentTorrent.downloaded),
        total: formatBytes(currentTorrent.length)
      });
    }, 1000);

    torrent.on('done', () => {
      clearInterval(interval);
      event.sender.send('webtorrent-done', { type, path: destPath });
      
      if (type === 'packed') {
        const unpackExe = findFileByName(destPath, 'unpack.exe', 3); // Adjust name if needed
        if (unpackExe) {
          spawn(unpackExe, [], { detached: true, stdio: 'ignore' }).unref();
        } else {
          // If the unpack tool isn't named unpack.exe, try to find any setup or bat
          const setupBat = findFileByName(destPath, 'setup.bat', 3);
          if (setupBat) {
            spawn(setupBat, [], { detached: true, stdio: 'ignore' }).unref();
          } else {
              // Try to find the first exe in the folder
              const firstExe = findFirstFileByExtensions(destPath, ['.exe'], 3);
              if (firstExe) spawn(firstExe, [], { detached: true, stdio: 'ignore' }).unref();
          }
        }
      }
      
      currentTorrent = null;
    });

    torrent.on('error', (err) => {
      clearInterval(interval);
      event.sender.send('webtorrent-error', err.message);
      currentTorrent = null;
    });
  });

  return { success: true };
});

ipcMain.handle('pause-resume-webtorrent', () => {
  if (currentTorrent) {
    if (currentTorrent.paused) {
      currentTorrent.resume();
    } else {
      currentTorrent.pause();
    }
  }
});

ipcMain.handle('cancel-webtorrent', () => {
  if (currentTorrent) {
    currentTorrent.destroy();
    currentTorrent = null;
  }
});

function findFirstFileByExtensions(rootDir, extensions, maxDepth = 5) {
    const stack = [{ dir: rootDir, depth: 0 }];

    while (stack.length > 0) {
        const current = stack.pop();
        if (current.depth > maxDepth) continue;

        let entries;
        try { entries = fs.readdirSync(current.dir, { withFileTypes: true }); } catch (e) { continue; }

        for (const entry of entries) {
            const entryPath = path.join(current.dir, entry.name);
            if (entry.isFile()) {
                const lowerName = entry.name.toLowerCase();
                if (extensions.some(extension => lowerName.endsWith(extension))) {
                    return entryPath;
                }
            }
            if (entry.isDirectory()) {
                stack.push({ dir: entryPath, depth: current.depth + 1 });
            }
        }
    }
    return '';
}