const { ipcRenderer } = require('electron');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');

// DOM elements
const gamePathInput = document.getElementById('game-path');
const selectFolderBtn = document.getElementById('select-folder-btn');
const installModBtn = document.getElementById('install-mod-btn');
const installResurgeOnlyBtn = document.getElementById('install-resurge-only-btn');
const cleanWgsCacheBtn = document.getElementById('clean-wgs-cache-btn');
const runMsStoreBtn = document.getElementById('run-ms-store-btn');
const themeToggleBtn = document.getElementById('theme-toggle-btn');
const windowMinimizeBtn = document.getElementById('window-minimize-btn');
const windowMaximizeBtn = document.getElementById('window-maximize-btn');
const windowCloseBtn = document.getElementById('window-close-btn');
const showGameFolderBtn = document.getElementById('show-game-folder-btn');
const openResurgeSettingsBtn = document.getElementById('open-resurge-settings-btn');
const openModsManagerBtn = document.getElementById('open-mods-manager-btn');
const importSteamWorkshopBtn = document.getElementById('import-steam-workshop-btn');
const loadGscInjectorBtn = document.getElementById('load-gsc-injector-btn');
const repairGamingServicesBtn = document.getElementById('repair-gaming-services-btn');
const discordBtn = document.getElementById('discord-btn');
const otherClientsBtn = document.getElementById('other-clients-btn');
const statusText = document.getElementById('status-text');
const resurgeSettingsModal = document.getElementById('resurge-settings-modal');
const closeResurgeSettingsBtn = document.getElementById('close-resurge-settings-btn');
const cancelResurgeSettingsBtn = document.getElementById('cancel-resurge-settings-btn');
const resurgeSettingsForm = document.getElementById('resurge-settings-form');
const resurgeIniPathLabel = document.getElementById('resurge-ini-path');
const modsManagerModal = document.getElementById('mods-manager-modal');
const closeModsManagerBtn = document.getElementById('close-mods-manager-btn');
const refreshModsManagerBtn = document.getElementById('refresh-mods-manager-btn');
const modsManagerContent = document.getElementById('mods-manager-content');
const modsManagerEmpty = document.getElementById('mods-manager-empty');
const modsManagerRootPath = document.getElementById('mods-manager-root-path');

const gscInjectorModal = document.getElementById('gsc-injector-modal');
const closeGscInjectorBtn = document.getElementById('close-gsc-injector-btn');
const refreshGscListBtn = document.getElementById('refresh-gsc-list-btn');
const gscListContent = document.getElementById('gsc-list-content');
const gscListEmpty = document.getElementById('gsc-list-empty');
const gscDirPath = document.getElementById('gsc-dir-path');

const downloadDowngradeBtn = document.getElementById('download-downgrade-btn');
const webtorrentModal = document.getElementById('webtorrent-modal');
const closeWebtorrentBtn = document.getElementById('close-webtorrent-btn');
const webtorrentSelection = document.getElementById('webtorrent-selection');
const webtorrentProgressContainer = document.getElementById('webtorrent-progress-container');
const downloadUnpackedBtn = document.getElementById('download-unpacked-btn');
const downloadPackedBtn = document.getElementById('download-packed-btn');
const webtorrentStatus = document.getElementById('webtorrent-status');
const webtorrentProgressBar = document.getElementById('webtorrent-progress-bar');
const webtorrentSpeed = document.getElementById('webtorrent-speed');
const webtorrentTime = document.getElementById('webtorrent-time');
const webtorrentPercentage = document.getElementById('webtorrent-percentage');
const webtorrentLog = document.getElementById('webtorrent-log');
const webtorrentPauseBtn = document.getElementById('webtorrent-pause-btn');
const webtorrentCancelBtn = document.getElementById('webtorrent-cancel-btn');

const resurgeSettingFields = {
    force_xbox_live: document.getElementById('force-xbox-live'),
    force_disable_steam: document.getElementById('force-disable-steam'),
    enable_console: document.getElementById('enable-console'),
    enable_unlock_all: document.getElementById('enable-unlock-all'),
    spoof_dlc_mappacks: document.getElementById('spoof-dlc-mappacks'),
    security_prefix: document.getElementById('security-prefix'),
    security_hooks: document.getElementById('security-hooks'),
    gobblegum_hack: document.getElementById('gobblegum-hack'),
    gobblegum_value: document.getElementById('gobblegum-value'),
    cryptokeys_hack: document.getElementById('cryptokeys-hack'),
    cryptokeys_value: document.getElementById('cryptokeys-value'),
    liquid_divinium_hack: document.getElementById('liquid-divinium-hack'),
    liquid_divinium_value: document.getElementById('liquid-divinium-value'),
    controller_icons: document.getElementById('controller-icons')
};

const defaultResurgeSettings = {
    force_xbox_live: '1',
    force_disable_steam: '2',
    enable_console: '1',
    enable_unlock_all: '1',
    spoof_dlc_mappacks: '1',
    security_prefix: '1',
    security_hooks: '1',
    gobblegum_hack: '1',
    gobblegum_value: '999',
    cryptokeys_hack: '0',
    cryptokeys_value: '99999',
    liquid_divinium_hack: '0',
    liquid_divinium_value: '99999',
    controller_icons: '1'
};

// State
let currentGamePath = '';
let currentResurgeIniPath = '';
let currentModsEntries = [];

// Initialize
document.addEventListener('DOMContentLoaded', () => {
    // Load saved game path
    ipcRenderer.on('game-path-loaded', (event, path) => {
        if (path) {
            setGamePath(path);
        }
    });
});

// Theme Toggle Logic
function loadTheme() {
    const savedTheme = localStorage.getItem('app-theme') || 'dark';
    if (savedTheme === 'light') {
        document.documentElement.setAttribute('data-theme', 'light');
        if (themeToggleBtn) themeToggleBtn.textContent = 'Dark Mode';
    } else {
        document.documentElement.removeAttribute('data-theme');
        if (themeToggleBtn) themeToggleBtn.textContent = 'Light Mode';
    }
}

loadTheme();

if (themeToggleBtn) {
    themeToggleBtn.addEventListener('click', () => {
        const currentTheme = document.documentElement.getAttribute('data-theme');
        if (currentTheme === 'light') {
            document.documentElement.removeAttribute('data-theme');
            localStorage.setItem('app-theme', 'dark');
            themeToggleBtn.textContent = 'Light Mode';
        } else {
            document.documentElement.setAttribute('data-theme', 'light');
            localStorage.setItem('app-theme', 'light');
            themeToggleBtn.textContent = 'Dark Mode';
        }
    });
}

if (windowMinimizeBtn) {
    windowMinimizeBtn.addEventListener('click', () => {
        ipcRenderer.invoke('window-minimize');
    });
}

if (windowMaximizeBtn) {
    windowMaximizeBtn.addEventListener('click', () => {
        ipcRenderer.invoke('window-maximize-toggle');
    });
}

if (windowCloseBtn) {
    windowCloseBtn.addEventListener('click', () => {
        ipcRenderer.invoke('window-close');
    });
}

// Event listeners
selectFolderBtn.addEventListener('click', async () => {
    setStatus('Selecting folder...', 'info');
    setButtonLoading(selectFolderBtn, true);

    try {
        const result = await ipcRenderer.invoke('select-game-folder');

        if (result.success) {
            setGamePath(result.path);
            setStatus('Game folder selected successfully', 'success');
        } else {
            setStatus(`Error: ${result.error}`, 'error');
        }
    } catch (error) {
        setStatus(`Error selecting folder: ${error.message}`, 'error');
    } finally {
        setButtonLoading(selectFolderBtn, false);
    }
});

installModBtn.addEventListener('click', async () => {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    setStatus('Installing Resurge + Boiii Client...', 'info');
    setButtonLoading(installModBtn, true);

    try {
        const result = await ipcRenderer.invoke('install-mod');

        if (result.success) {
            setStatus('Clients installed successfully', 'success');
        } else {
            setStatus(`Installation failed: ${result.error}`, 'error');
        }
    } catch (error) {
        setStatus(`Installation error: ${error.message}`, 'error');
    } finally {
        setButtonLoading(installModBtn, false);
    }
});

installResurgeOnlyBtn.addEventListener('click', async () => {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    setStatus('Installing Resurge Client...', 'info');
    setButtonLoading(installResurgeOnlyBtn, true);

    try {
        const result = await ipcRenderer.invoke('install-resurge-only');

        if (result.success) {
            setStatus('Resurge Client installed successfully', 'success');
        } else {
            setStatus(`Installation failed: ${result.error}`, 'error');
        }
    } catch (error) {
        setStatus(`Installation error: ${error.message}`, 'error');
    } finally {
        setButtonLoading(installResurgeOnlyBtn, false);
    }
});

openResurgeSettingsBtn.addEventListener('click', async () => {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    setStatus('Loading Resurge.ini settings...', 'info');
    setButtonLoading(openResurgeSettingsBtn, true);

    try {
        const iniPath = resolveResurgeIniPath();

        if (!iniPath) {
            setStatus('Resurge.ini not found in the selected game folder', 'error');
            return;
        }

        currentResurgeIniPath = iniPath;
        resurgeIniPathLabel.textContent = iniPath;

        const iniText = fs.readFileSync(iniPath, 'utf8');
        const parsedSettings = parseResurgeIni(iniText);
        populateResurgeSettingsForm(parsedSettings);

        openResurgeSettingsModal();
        setStatus('Resurge.ini loaded', 'success');
    } catch (error) {
        setStatus(`Failed to load Resurge.ini: ${error.message}`, 'error');
    } finally {
        setButtonLoading(openResurgeSettingsBtn, false);
    }
});

openModsManagerBtn.addEventListener('click', () => {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    loadModsManager();
    openModsManagerModal();
});

importSteamWorkshopBtn.addEventListener('click', async () => {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    setStatus('Importing Steam Workshop mods/maps...', 'info');
    setButtonLoading(importSteamWorkshopBtn, true);

    try {
        const result = await ipcRenderer.invoke('import-steam-workshop-mods-maps');

        if (result.success) {
            setStatus(result.message || 'Steam Workshop content imported successfully', 'success');
            if (modsManagerModal && modsManagerModal.style.display === 'block') {
                loadModsManager();
            }
        } else {
            setStatus(`Import failed: ${result.error}`, 'error');
        }
    } catch (error) {
        setStatus(`Import error: ${error.message}`, 'error');
    } finally {
        setButtonLoading(importSteamWorkshopBtn, false);
    }
});

loadGscInjectorBtn.addEventListener('click', async () => {
    await loadGscInjectorList();
    openGscInjectorModal();
});

closeGscInjectorBtn.addEventListener('click', () => {
    closeGscInjectorModal();
});

refreshGscListBtn.addEventListener('click', loadGscInjectorList);

gscInjectorModal.addEventListener('click', (event) => {
    if (event.target === gscInjectorModal) {
        closeGscInjectorModal();
    }
});

closeResurgeSettingsBtn.addEventListener('click', closeResurgeSettingsModal);
cancelResurgeSettingsBtn.addEventListener('click', closeResurgeSettingsModal);

resurgeSettingsModal.addEventListener('click', (event) => {
    if (event.target === resurgeSettingsModal) {
        closeResurgeSettingsModal();
    }
});

resurgeSettingsForm.addEventListener('submit', (event) => {
    event.preventDefault();
    saveResurgeSettings();
});

closeModsManagerBtn.addEventListener('click', closeModsManagerModal);
refreshModsManagerBtn.addEventListener('click', loadModsManager);

modsManagerModal.addEventListener('click', (event) => {
    if (event.target === modsManagerModal) {
        closeModsManagerModal();
    }
});

cleanWgsCacheBtn.addEventListener('click', async () => {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    setStatus('Cleaning WGS cache folder...', 'info');
    setButtonLoading(cleanWgsCacheBtn, true);

    try {
        const result = await ipcRenderer.invoke('clean-wgs-cache-folder');

        if (result.success) {
            setStatus(result.message || 'WGS cache folder cleaned successfully', 'success');
        } else {
            setStatus(`Cleanup failed: ${result.error}`, 'error');
        }
    } catch (error) {
        setStatus(`Cleanup error: ${error.message}`, 'error');
    } finally {
        setButtonLoading(cleanWgsCacheBtn, false);
    }
});

discordBtn.addEventListener('click', () => {
    ipcRenderer.invoke('open-url', 'https://discord.gg/dpmRyDfbnM');
});

otherClientsBtn.addEventListener('click', () => {
    ipcRenderer.invoke('open-url', 'https://discord.gg/dpmRyDfbnM');
});

repairGamingServicesBtn.addEventListener('click', async () => {
    setStatus('Downloading and running Xbox Gaming Services Repair Tool...', 'info');
    setButtonLoading(repairGamingServicesBtn, true);

    try {
        const result = await ipcRenderer.invoke('repair-gaming-services');

        if (result.success) {
            setStatus('Repair Tool launched successfully', 'success');
        } else {
            setStatus(`Failed to launch tool: ${result.error}`, 'error');
        }
    } catch (error) {
        setStatus(`Launch error: ${error.message}`, 'error');
    } finally {
        setButtonLoading(repairGamingServicesBtn, false);
    }
});

downloadDowngradeBtn.addEventListener('click', () => {
    webtorrentSelection.style.display = 'block';
    webtorrentProgressContainer.style.display = 'none';
    webtorrentModal.style.display = 'block';
    webtorrentModal.setAttribute('aria-hidden', 'false');
});

closeWebtorrentBtn.addEventListener('click', () => {
    webtorrentModal.style.display = 'none';
    webtorrentModal.setAttribute('aria-hidden', 'true');
});

function handleWebtorrentSelection(type) {
    webtorrentSelection.style.display = 'none';
    webtorrentProgressContainer.style.display = 'block';
    webtorrentStatus.textContent = 'Starting download...';
    webtorrentProgressBar.style.width = '0%';
    webtorrentPercentage.textContent = '0%';
    webtorrentLog.textContent = 'Looking for peers...';
    
    ipcRenderer.invoke('start-webtorrent-download', type).then(res => {
        if (!res.success) {
            webtorrentStatus.textContent = `Error: ${res.error}`;
        }
    });
}

downloadUnpackedBtn.addEventListener('click', () => handleWebtorrentSelection('unpacked'));
downloadPackedBtn.addEventListener('click', () => handleWebtorrentSelection('packed'));

webtorrentPauseBtn.addEventListener('click', () => {
    ipcRenderer.invoke('pause-resume-webtorrent');
});

webtorrentCancelBtn.addEventListener('click', () => {
    ipcRenderer.invoke('cancel-webtorrent');
    webtorrentModal.style.display = 'none';
    webtorrentModal.setAttribute('aria-hidden', 'true');
});

// IPC Listeners for WebTorrent
ipcRenderer.on('webtorrent-progress', (event, data) => {
    webtorrentProgressBar.style.width = `${data.percentage}%`;
    webtorrentPercentage.textContent = `${data.percentage.toFixed(1)}%`;
    webtorrentSpeed.textContent = data.speed;
    webtorrentTime.textContent = data.timeRemaining;
    webtorrentLog.textContent = `Connected to ${data.numPeers} peers. Downloaded ${data.downloaded} of ${data.total}`;
    if (data.paused) {
        webtorrentStatus.textContent = 'Paused';
        webtorrentPauseBtn.textContent = 'Resume';
    } else {
        webtorrentStatus.textContent = 'Downloading...';
        webtorrentPauseBtn.textContent = 'Pause';
    }
});

ipcRenderer.on('webtorrent-done', (event, data) => {
    webtorrentStatus.textContent = 'Download Complete!';
    webtorrentSpeed.textContent = '';
    webtorrentTime.textContent = '';
    webtorrentLog.textContent = '';
    webtorrentProgressBar.style.width = '100%';
    webtorrentPercentage.textContent = '100%';
    
    if (data.type === 'unpacked' && data.path) {
        setGamePath(data.path);
        setStatus('Download finished. Please click on "Install Resurge + Boiii Client" to complete.', 'success');
        setTimeout(() => {
            webtorrentModal.style.display = 'none';
            webtorrentModal.setAttribute('aria-hidden', 'true');
        }, 3000);
    } else if (data.type === 'packed') {
        setStatus('Download finished. Unpack tool starting...', 'info');
        setTimeout(() => {
            webtorrentModal.style.display = 'none';
            webtorrentModal.setAttribute('aria-hidden', 'true');
        }, 3000);
    }
});

ipcRenderer.on('webtorrent-error', (event, error) => {
    webtorrentStatus.textContent = `Error: ${error}`;
});

// Launch button handlers
runMsStoreBtn.addEventListener('click', () => runExecutable('Resurge - Beta.exe', 'Resurge Client'));
showGameFolderBtn.addEventListener('click', () => {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }
    ipcRenderer.invoke('show-game-folder');
});

// Helper functions
function setGamePath(path) {
    currentGamePath = path;
    gamePathInput.value = path;

    // Enable buttons when game path is set
    const buttonsToEnable = [installModBtn, installResurgeOnlyBtn, cleanWgsCacheBtn, runMsStoreBtn, showGameFolderBtn, openResurgeSettingsBtn, openModsManagerBtn, importSteamWorkshopBtn, discordBtn, otherClientsBtn];
    buttonsToEnable.forEach(btn => btn.disabled = false);
}

function setStatus(message, type = 'info') {
    statusText.textContent = message;
    statusText.className = `status-${type}`;

    // Clear status after 5 seconds for non-error messages
    if (type !== 'error') {
        setTimeout(() => {
            statusText.textContent = 'Ready';
            statusText.className = '';
        }, 5000);
    }
}

function setButtonLoading(button, isLoading) {
    if (isLoading) {
        button.disabled = true;
        button.classList.add('loading');
        button.dataset.originalText = button.textContent;
        button.textContent = 'Processing...';
    } else {
        button.disabled = !currentGamePath; // Re-enable only if game path is set
        button.classList.remove('loading');
        button.textContent = button.dataset.originalText || button.textContent;
    }
}

async function runExecutable(exeName, description) {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    setStatus(`Launching ${description}...`, 'info');

    try {
        const result = await ipcRenderer.invoke('run-executable', exeName);

        if (result.success) {
            setStatus(`${description} launched successfully`, 'success');
        } else {
            setStatus(`Failed to launch ${description}: ${result.error}`, 'error');
        }
    } catch (error) {
        setStatus(`Launch error: ${error.message}`, 'error');
    }
}

function openResurgeSettingsModal() {
    resurgeSettingsModal.style.display = 'block';
    resurgeSettingsModal.setAttribute('aria-hidden', 'false');
    document.body.style.overflow = 'hidden';
}

function closeResurgeSettingsModal() {
    resurgeSettingsModal.style.display = 'none';
    resurgeSettingsModal.setAttribute('aria-hidden', 'true');
    document.body.style.overflow = '';
}

function openModsManagerModal() {
    modsManagerModal.style.display = 'block';
    modsManagerModal.setAttribute('aria-hidden', 'false');
    document.body.style.overflow = 'hidden';
}

function closeModsManagerModal() {
    modsManagerModal.style.display = 'none';
    modsManagerModal.setAttribute('aria-hidden', 'true');
    document.body.style.overflow = '';
}

function openGscInjectorModal() {
    gscInjectorModal.style.display = 'block';
    gscInjectorModal.setAttribute('aria-hidden', 'false');
    document.body.style.overflow = 'hidden';
}

function closeGscInjectorModal() {
    gscInjectorModal.style.display = 'none';
    gscInjectorModal.setAttribute('aria-hidden', 'true');
    document.body.style.overflow = '';
}

function resolveResurgeIniPath() {
    const candidates = [
        path.join(currentGamePath, 'ENGLISH', 'Resurge.ini'),
        path.join(currentGamePath, 'Resurge.ini')
    ];

    for (const candidate of candidates) {
        if (fs.existsSync(candidate)) {
            return candidate;
        }
    }

    return findFileByName(currentGamePath, 'Resurge.ini', 4);
}

function findFileByName(rootDir, targetFileName, maxDepth = 4) {
    const stack = [{ dir: rootDir, depth: 0 }];

    while (stack.length > 0) {
        const current = stack.pop();

        if (current.depth > maxDepth) {
            continue;
        }

        const entries = fs.readdirSync(current.dir, { withFileTypes: true });

        for (const entry of entries) {
            const entryPath = path.join(current.dir, entry.name);

            if (entry.isFile() && entry.name.toLowerCase() === targetFileName.toLowerCase()) {
                return entryPath;
            }

            if (entry.isDirectory()) {
                stack.push({ dir: entryPath, depth: current.depth + 1 });
            }
        }
    }

    return '';
}

function parseResurgeIni(iniText) {
    const values = { ...defaultResurgeSettings };
    const lines = iniText.split(/\r?\n/);

    for (const line of lines) {
        const trimmedLine = line.trim();

        if (!trimmedLine || trimmedLine.startsWith(';') || trimmedLine.startsWith('[')) {
            continue;
        }

        const separatorIndex = trimmedLine.indexOf('=');
        if (separatorIndex === -1) {
            continue;
        }

        const key = trimmedLine.slice(0, separatorIndex).trim();
        const value = trimmedLine.slice(separatorIndex + 1).trim();

        if (key in values) {
            values[key] = value;
        }
    }

    return values;
}

function populateResurgeSettingsForm(settings) {
    for (const [key, field] of Object.entries(resurgeSettingFields)) {
        if (!field) {
            continue;
        }

        field.value = settings[key] ?? defaultResurgeSettings[key] ?? '0';
    }
}

function readResurgeSettingsForm() {
    const values = {};

    for (const [key, field] of Object.entries(resurgeSettingFields)) {
        if (!field) {
            continue;
        }

        values[key] = field.value;
    }

    return values;
}

function saveResurgeSettings() {
    if (!currentResurgeIniPath || !fs.existsSync(currentResurgeIniPath)) {
        setStatus('Resurge.ini path is not available', 'error');
        return;
    }

    try {
        const iniText = fs.readFileSync(currentResurgeIniPath, 'utf8');
        const updatedIniText = applyResurgeSettingsToIni(iniText, readResurgeSettingsForm());
        fs.writeFileSync(currentResurgeIniPath, updatedIniText, 'utf8');

        closeResurgeSettingsModal();
        setStatus('Resurge.ini updated successfully', 'success');
    } catch (error) {
        setStatus(`Failed to save Resurge.ini: ${error.message}`, 'error');
    }
}

function loadModsManager() {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    try {
        currentModsEntries = scanModsEntries();
        modsManagerRootPath.textContent = currentGamePath;
        renderModsManager(currentModsEntries);

        if (!modsManagerModal || modsManagerModal.style.display !== 'block') {
            openModsManagerModal();
        }

        setStatus(currentModsEntries.length > 0 ? 'Mods Manager loaded' : 'No mods or maps found', currentModsEntries.length > 0 ? 'success' : 'info');
    } catch (error) {
        setStatus(`Failed to load Mods Manager: ${error.message}`, 'error');
    }
}

function scanModsEntries() {
    const entries = [];
    const roots = [
        { label: 'mods', dir: path.join(currentGamePath, 'mods') },
        { label: 'usermaps', dir: path.join(currentGamePath, 'usermaps') }
    ];

    for (const root of roots) {
        if (!fs.existsSync(root.dir) || !fs.statSync(root.dir).isDirectory()) {
            continue;
        }

        const childEntries = fs.readdirSync(root.dir, { withFileTypes: true });

        for (const childEntry of childEntries) {
            if (!childEntry.isDirectory()) {
                continue;
            }

            const folderPath = path.join(root.dir, childEntry.name);
            const workshopPath = findFileByName(folderPath, 'workshop.json', 5);
            const workshopData = workshopPath ? readJsonFile(workshopPath) : null;
            const iconPath = findFirstFileByExtensions(folderPath, ['.png', '.jpg', '.jpeg'], 5);
            const zonePath = path.join(folderPath, 'zone');
            const looseFiles = getLooseFiles(folderPath);
            const folderName = childEntry.name;

            const rootEntries = fs.readdirSync(folderPath);
            const needsZoneFix = rootEntries.some(name => {
                const nameLower = name.toLowerCase();
                return nameLower !== 'zone' && nameLower !== 'workshop.json';
            });

            entries.push({
                type: root.label,
                rootDir: root.dir,
                folderName,
                folderPath,
                workshopPath,
                workshopData,
                iconPath,
                zonePath,
                looseFiles,
                workshopTitle: getWorkshopTitle(workshopData),
                workshopFolderName: workshopData && typeof workshopData.FolderName === 'string' ? workshopData.FolderName.trim() : '',
                needsRename: shouldRenameFolder(folderName, workshopData),
                needsZoneFix: needsZoneFix
            });
        }
    }

    return entries;
}

function renderModsManager(entries) {
    modsManagerContent.innerHTML = '';
    modsManagerEmpty.hidden = entries.length > 0;

    for (const entry of entries) {
        const card = document.createElement('div');
        card.className = 'mod-card';

        const iconMarkup = entry.iconPath
            ? `<img class="mod-icon" src="${pathToFileURL(entry.iconPath).href}" alt="${escapeHtml(entry.workshopTitle || entry.folderName)} icon">`
            : `<div class="mod-icon-placeholder">No icon</div>`;

        card.innerHTML = `
            ${iconMarkup}
            <div class="mod-card-body">
                <h4 class="mod-card-title">${escapeHtml(entry.workshopTitle || entry.folderName)}</h4>
                <p class="mod-card-meta">Type: ${escapeHtml(entry.type.toUpperCase())}</p>
                <p class="mod-card-meta">Folder: ${escapeHtml(entry.folderName)}</p>
                <p class="mod-card-meta">Workshop Folder: ${escapeHtml(entry.workshopFolderName || 'Not found')}</p>
                <p class="mod-card-meta">Workshop JSON: ${escapeHtml(entry.workshopPath || 'Not found')}</p>
                <p class="mod-card-meta">Zone: ${entry.needsZoneFix ? 'Needs fix' : 'OK'}</p>
                <div class="mod-card-actions">
                    <button type="button" class="btn btn-modsaction" data-mod-action="fix" data-mod-folder="${escapeHtmlAttr(entry.folderPath)}">Fix ${escapeHtml(entry.type.slice(0, -1))}</button>
                    <button type="button" class="btn btn-modsaction" data-mod-action="delete" data-mod-folder="${escapeHtmlAttr(entry.folderPath)}">Delete ${escapeHtml(entry.type.slice(0, -1))}</button>
                </div>
            </div>
        `;

        modsManagerContent.appendChild(card);
    }

    modsManagerContent.querySelectorAll('[data-mod-action]').forEach(button => {
        button.addEventListener('click', () => {
            const action = button.dataset.modAction;
            const folderPath = button.dataset.modFolder;
            const targetEntry = currentModsEntries.find(entry => entry.folderPath === folderPath);

            if (!targetEntry) {
                setStatus('Selected mod entry is no longer available', 'error');
                return;
            }

            if (action === 'fix') {
                fixModEntry(targetEntry);
            } else if (action === 'delete') {
                deleteModEntry(targetEntry);
            }
        });
    });
}

function fixModEntry(entry) {
    try {
        let workingFolderPath = entry.folderPath;

        if (entry.needsRename && entry.workshopFolderName && entry.folderName !== entry.workshopFolderName) {
            const targetFolderPath = path.join(entry.rootDir, entry.workshopFolderName);

            if (fs.existsSync(targetFolderPath)) {
                throw new Error(`Target folder already exists: ${entry.workshopFolderName}`);
            }

            fs.renameSync(workingFolderPath, targetFolderPath);
            workingFolderPath = targetFolderPath;
        }

        const zonePath = path.join(workingFolderPath, 'zone');
        if (!fs.existsSync(zonePath)) {
            fs.mkdirSync(zonePath, { recursive: true });
        }

        const rootEntries = fs.readdirSync(workingFolderPath, { withFileTypes: true });
        for (const rootEntry of rootEntries) {
            const entryNameLower = rootEntry.name.toLowerCase();
            if (entryNameLower === 'zone' || entryNameLower === 'workshop.json') {
                continue;
            }
            const srcPath = path.join(workingFolderPath, rootEntry.name);
            const destPath = path.join(zonePath, rootEntry.name);
            moveOrMergeSync(srcPath, destPath);
        }

        setStatus('Mod/map fixed successfully', 'success');
        loadModsManager();
    } catch (error) {
        setStatus(`Failed to fix mod/map: ${error.message}`, 'error');
    }
}

function deleteModEntry(entry) {
    const confirmed = window.confirm(`Delete ${entry.workshopTitle || entry.folderName}? This cannot be undone.`);
    if (!confirmed) {
        return;
    }

    try {
        fs.rmSync(entry.folderPath, { recursive: true, force: true });
        setStatus('Mod/map deleted successfully', 'success');
        loadModsManager();
    } catch (error) {
        setStatus(`Failed to delete mod/map: ${error.message}`, 'error');
    }
}

function applyResurgeSettingsToIni(iniText, settings) {
    let updatedText = iniText;

    for (const [key, value] of Object.entries(settings)) {
        const keyPattern = new RegExp(`(^\\s*${escapeRegExp(key)}\\s*=\\s*).*$`, 'm');

        if (keyPattern.test(updatedText)) {
            updatedText = updatedText.replace(keyPattern, `$1${value}`);
        } else {
            if (!updatedText.endsWith('\n')) {
                updatedText += '\n';
            }
            updatedText += `${key}=${value}\n`;
        }
    }

    return updatedText;
}

function escapeRegExp(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function escapeHtml(value) {
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function escapeHtmlAttr(value) {
    return escapeHtml(value).replace(/`/g, '&#96;');
}

function readJsonFile(filePath) {
    try {
        return JSON.parse(fs.readFileSync(filePath, 'utf8'));
    } catch {
        return null;
    }
}

function getWorkshopTitle(workshopData) {
    const title = workshopData && typeof workshopData.Title === 'string'
        ? workshopData.Title.trim()
        : '';

    return title ? formatDisplayTitle(title) : '';
}

function formatDisplayTitle(value) {
    return String(value)
        .replace(/[_-]+/g, ' ')
        .trim()
        .toUpperCase();
}

function shouldRenameFolder(folderName, workshopData) {
    if (!workshopData || typeof workshopData.FolderName !== 'string') {
        return false;
    }

    const targetFolderName = workshopData.FolderName.trim();
    if (!targetFolderName) {
        return false;
    }

    return /^\d+$/.test(folderName) && folderName !== targetFolderName;
}

function findFirstFileByExtensions(rootDir, extensions, maxDepth = 5) {
    const stack = [{ dir: rootDir, depth: 0 }];

    while (stack.length > 0) {
        const current = stack.pop();

        if (current.depth > maxDepth) {
            continue;
        }

        const entries = fs.readdirSync(current.dir, { withFileTypes: true });

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

function getLooseFiles(folderPath) {
    const entries = fs.readdirSync(folderPath, { withFileTypes: true });
    return entries
        .filter(entry => entry.isFile())
        .map(entry => path.join(folderPath, entry.name));
}

function movePathSync(sourcePath, destinationPath) {
    try {
        fs.renameSync(sourcePath, destinationPath);
    } catch (error) {
        fs.copyFileSync(sourcePath, destinationPath);
        fs.unlinkSync(sourcePath);
    }
}

function moveOrMergeSync(src, dest) {
    const stat = fs.statSync(src);
    if (stat.isDirectory()) {
        if (!fs.existsSync(dest)) {
            fs.mkdirSync(dest, { recursive: true });
        }
        const entries = fs.readdirSync(src, { withFileTypes: true });
        for (const entry of entries) {
            moveOrMergeSync(path.join(src, entry.name), path.join(dest, entry.name));
        }
        fs.rmdirSync(src);
    } else {
        if (fs.existsSync(dest)) {
            fs.rmSync(dest, { force: true });
        }
        movePathSync(src, dest);
    }
}

// Add CSS classes for status styling
const style = document.createElement('style');
style.textContent = `
    .status-info { color: #17a2b8; }
    .status-success { color: #4CAF50; }
    .status-error { color: #f44336; }
`;
document.head.appendChild(style);

// GSC Injector UI logic
async function loadGscInjectorList() {
    if (!currentGamePath) {
        setStatus('Please select game folder first', 'error');
        return;
    }

    setStatus('Loading GSC list...', 'info');
    setButtonLoading(loadGscInjectorBtn, true);

    try {
        const result = await ipcRenderer.invoke('get-gsc-list');

        if (!result || !result.success) {
            setStatus(`Failed to load GSC list: ${result && result.error ? result.error : 'unknown error'}`, 'error');
            return;
        }

        const files = Array.isArray(result.files) ? result.files : [];
        gscDirPath.textContent = result.dir || '';
        gscListContent.innerHTML = '';
        gscListEmpty.hidden = files.length > 0;

        for (const fileName of files) {
            const entry = document.createElement('div');
            entry.className = 'gsc-entry';

            const nameEl = document.createElement('span');
            nameEl.className = 'gsc-name';
            nameEl.textContent = fileName;

            const injectBtn = document.createElement('button');
            injectBtn.className = 'btn btn-inject';
            injectBtn.type = 'button';
            injectBtn.textContent = 'Inject';

            injectBtn.addEventListener('click', async () => {
                const origText = injectBtn.textContent;
                setButtonLoading(injectBtn, true);
                try {
                    setStatus(`Injecting ${fileName}...`, 'info');
                    const res = await ipcRenderer.invoke('inject-gsc', fileName);

                    if (res && res.success) {
                        setStatus(`Injected ${fileName} successfully`, 'success');
                    } else {
                        setStatus(`Injection failed: ${res && res.error ? res.error : 'unknown error'}`, 'error');
                    }
                } catch (err) {
                    setStatus(`Injection error: ${err.message}`, 'error');
                } finally {
                    setButtonLoading(injectBtn, false);
                    injectBtn.textContent = origText;
                }
            });

            entry.appendChild(nameEl);
            entry.appendChild(injectBtn);
            gscListContent.appendChild(entry);
        }

        setStatus(files.length > 0 ? 'GSC list loaded' : 'No .gsc files found', files.length > 0 ? 'success' : 'info');
    } catch (error) {
        setStatus(`Failed to load GSC list: ${error.message}`, 'error');
    } finally {
        setButtonLoading(loadGscInjectorBtn, false);
    }
}