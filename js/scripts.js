const residentForm = document.getElementById('residentForm');
const video = document.getElementById('myVideo');
const startCameraButton = document.getElementById('startCameraBtn');
const stopCameraButton = document.getElementById('stopCameraBtn');
const simulateRecognitionButton = document.getElementById('simulateRecognitionBtn');
const submitResidentButton = residentForm.querySelector('button[type="submit"]');
const formStatus = document.getElementById('formStatus');
const addressStatus = document.getElementById('addressStatus');
const recognitionStatus = document.getElementById('recognitionStatus');
const residentPreview = document.getElementById('residentPreview');
const residentCount = document.getElementById('residentCount');
const cameraState = document.getElementById('cameraState');
const apiStatus = document.getElementById('apiStatus');
const privacyTrail = document.getElementById('privacyTrail');
const auditCount = document.getElementById('auditCount');
const emergencyAlertsPanel = document.getElementById('emergencyAlerts');
const emergencyAlertCount = document.getElementById('emergencyAlertCount');
const cityStateDisplay = document.getElementById('cityStateDisplay');
const processAlertsButton = document.getElementById('processAlertsBtn');

const formFields = {
    name: residentForm.elements.namedItem('name'),
    cpf: residentForm.elements.namedItem('cpf'),
    postalCode: residentForm.elements.namedItem('postalCode'),
    street: residentForm.elements.namedItem('street'),
    number: residentForm.elements.namedItem('number'),
    neighborhood: residentForm.elements.namedItem('neighborhood'),
    emergencyContactName: residentForm.elements.namedItem('emergencyContactName'),
    emergencyContactPhone: residentForm.elements.namedItem('emergencyContactPhone'),
    emergencyContactEmail: residentForm.elements.namedItem('emergencyContactEmail'),
    emergencyContactRelationship: residentForm.elements.namedItem('emergencyContactRelationship'),
    alertDestination: residentForm.elements.namedItem('alertDestination'),
    consent: residentForm.elements.namedItem('consent'),
};

const appState = {
    residents: [],
    privacyEvents: [],
    emergencyAlerts: [],
    stream: null,
    apiOnline: false,
    faceEncodingReady: false,
    addressLookup: null,
    addressLookupError: '',
    addressLookupToken: 0,
};

const displayTextReplacements = [
    [/\bTeresopolis\b/g, 'Teresópolis'],
    [/\bServico\b/g, 'Serviço'],
    [/\bservico\b/g, 'serviço'],
    [/\bConfirmacao\b/g, 'Confirmação'],
    [/\bconfirmacao\b/g, 'confirmação'],
    [/\bValidacao\b/g, 'Validação'],
    [/\bvalidacao\b/g, 'validação'],
    [/\bEndereco\b/g, 'Endereço'],
    [/\bendereco\b/g, 'endereço'],
    [/\bEmergencia\b/g, 'Emergência'],
    [/\bemergencia\b/g, 'emergência'],
    [/\bExclusao\b/g, 'Exclusão'],
    [/\bexclusao\b/g, 'exclusão'],
    [/\bRevogacao\b/g, 'Revogação'],
    [/\brevogacao\b/g, 'revogação'],
    [/\bHistorico\b/g, 'Histórico'],
    [/\bhistorico\b/g, 'histórico'],
    [/\bAutomatico\b/g, 'Automático'],
    [/\bautomatico\b/g, 'automático'],
    [/\bAutomatica\b/g, 'Automática'],
    [/\bautomatica\b/g, 'automática'],
    [/\bPublico\b/g, 'Público'],
    [/\bpublico\b/g, 'público'],
    [/\bPublica\b/g, 'Pública'],
    [/\bpublica\b/g, 'pública'],
    [/\bPublicos\b/g, 'Públicos'],
    [/\bpublicos\b/g, 'públicos'],
    [/\bOrgao\b/g, 'Órgão'],
    [/\borgao\b/g, 'órgão'],
    [/\bOrgaos\b/g, 'Órgãos'],
    [/\borgaos\b/g, 'órgãos'],
    [/\bUltima\b/g, 'Última'],
    [/\bultima\b/g, 'última'],
    [/\bNumero\b/g, 'Número'],
    [/\bnumero\b/g, 'número'],
    [/\bRelacao\b/g, 'Relação'],
    [/\brelacao\b/g, 'relação'],
    [/\bPresenca\b/g, 'Presença'],
    [/\bpresenca\b/g, 'presença'],
    [/\bAusencia\b/g, 'Ausência'],
    [/\bausencia\b/g, 'ausência'],
    [/\bSeguranca\b/g, 'Segurança'],
    [/\bseguranca\b/g, 'segurança'],
    [/\bOperacao\b/g, 'Operação'],
    [/\boperacao\b/g, 'operação'],
    [/\bComunicacao\b/g, 'Comunicação'],
    [/\bcomunicacao\b/g, 'comunicação'],
    [/\bConfianca\b/g, 'Confiança'],
    [/\bconfianca\b/g, 'confiança'],
    [/\bConcluido\b/g, 'Concluído'],
    [/\bconcluido\b/g, 'concluído'],
    [/\bConcluidas\b/g, 'Concluídas'],
    [/\bconcluidas\b/g, 'concluídas'],
    [/\bDisponivel\b/g, 'Disponível'],
    [/\bdisponivel\b/g, 'disponível'],
    [/\bDisponiveis\b/g, 'Disponíveis'],
    [/\bdisponiveis\b/g, 'disponíveis'],
    [/\bValido\b/g, 'Válido'],
    [/\bvalido\b/g, 'válido'],
    [/\bPeriodico\b/g, 'Periódico'],
    [/\bperiodico\b/g, 'periódico'],
    [/\bCondominio\b/g, 'Condomínio'],
    [/\bcondominio\b/g, 'condomínio'],
    [/\bProxima\b/g, 'Próxima'],
    [/\bproxima\b/g, 'próxima'],
    [/\bPropria\b/g, 'Própria'],
    [/\bpropria\b/g, 'própria'],
    [/\bPermissoes\b/g, 'Permissões'],
    [/\bpermissoes\b/g, 'permissões'],
    [/\bCamera\b/g, 'Câmera'],
    [/\bcamera\b/g, 'câmera'],
    [/\bProtecao\b/g, 'Proteção'],
    [/\bprotecao\b/g, 'proteção'],
    [/\bVoce\b/g, 'Você'],
    [/\bvoce\b/g, 'você'],
    [/\bNao\b/g, 'Não'],
    [/\bnao\b/g, 'não'],
    [/\bAte\b/g, 'Até'],
    [/\bate\b/g, 'até'],
    [/\bTambem\b/g, 'Também'],
    [/\btambem\b/g, 'também'],
    [/\bSera\b/g, 'Será'],
    [/\bsera\b/g, 'será'],
    [/\bHa\b/g, 'Há'],
    [/\bha\b/g, 'há'],
    [/\bPossivel\b/g, 'Possível'],
    [/\bpossivel\b/g, 'possível'],
];

const trackedFields = [
    formFields.name,
    formFields.cpf,
    formFields.postalCode,
    formFields.street,
    formFields.number,
    formFields.neighborhood,
    formFields.emergencyContactName,
    formFields.emergencyContactPhone,
    formFields.emergencyContactEmail,
    formFields.emergencyContactRelationship,
    formFields.alertDestination,
];

let suppressNextResetMessage = false;

renderResidentPreview();
renderPrivacyTrail();
renderEmergencyAlerts();
boot();

residentForm.addEventListener('submit', handleResidentSubmit);
residentForm.addEventListener('reset', handleFormReset);
residentForm.addEventListener('input', handleResidentInput);
residentForm.addEventListener('focusout', handleResidentFieldBlur);
residentPreview.addEventListener('click', handleResidentActionClick);
startCameraButton.addEventListener('click', startCamera);
stopCameraButton.addEventListener('click', stopCamera);
simulateRecognitionButton.addEventListener('click', simulateRecognition);
processAlertsButton.addEventListener('click', processPendingAlerts);
window.addEventListener('beforeunload', stopCamera);

async function boot() {
    cityStateDisplay.value = 'Teresópolis / RJ';
    await refreshBackendState();
}

async function refreshBackendState() {
    const health = await checkApiHealth();

    if (!health) {
        appState.residents = [];
        appState.privacyEvents = [];
        appState.emergencyAlerts = [];
        renderResidentPreview();
        renderPrivacyTrail();
        renderEmergencyAlerts();
        return;
    }

    await refreshPilotData();
}

async function refreshPilotData() {
    await Promise.all([loadResidents(), loadPrivacyEvents(), loadEmergencyAlerts()]);
}

function formatDisplayText(value) {
    if (typeof value !== 'string') {
        return value;
    }

    return displayTextReplacements.reduce(
        (text, [pattern, replacement]) => text.replace(pattern, replacement),
        value
    );
}

function setMessage(element, message, tone = 'neutral') {
    element.textContent = formatDisplayText(message);
    element.dataset.tone = tone;
}

function setBackendState({ apiOnline, faceEncodingReady, label }) {
    appState.apiOnline = apiOnline;
    appState.faceEncodingReady = faceEncodingReady;
    submitResidentButton.disabled = !faceEncodingReady;
    simulateRecognitionButton.disabled = !faceEncodingReady;
    processAlertsButton.disabled = !apiOnline;
    apiStatus.textContent = formatDisplayText(label);
    apiStatus.classList.toggle('live', apiOnline && faceEncodingReady);
    apiStatus.classList.toggle('warning', apiOnline && !faceEncodingReady);
}

async function checkApiHealth() {
    try {
        const response = await fetch('/api/health', {
            headers: {
                Accept: 'application/json',
            },
        });

        if (!response.ok) {
            throw new Error('O serviço não respondeu com sucesso.');
        }

        const health = await response.json();
        const faceEncodingReady = Boolean(health.faceEncodingReady);

        setBackendState({
            apiOnline: true,
            faceEncodingReady,
            label: faceEncodingReady ? 'serviço + biometria online' : 'serviço online / biometria indisponível',
        });

        setMessage(
            formStatus,
            faceEncodingReady
                ? 'Cadastro, validação de endereço e reconhecimento facial estão disponíveis.'
                : 'A confirmação facial ainda não está pronta. Aguarde alguns instantes e tente novamente.',
            faceEncodingReady ? 'success' : 'neutral'
        );

        return health;
    } catch (error) {
        console.error('Falha ao consultar o serviço:', error);
        setBackendState({
            apiOnline: false,
            faceEncodingReady: false,
            label: 'serviço offline',
        });
        setMessage(
            formStatus,
            'O serviço de cadastro e confirmação facial não está disponível agora.',
            'error'
        );

        return null;
    }
}

async function loadPrivacyEvents() {
    try {
        const response = await fetch('/api/privacy-events', {
            headers: {
                Accept: 'application/json',
            },
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        const events = await response.json();
        appState.privacyEvents = Array.isArray(events) ? events : [];
        renderPrivacyTrail();
    } catch (error) {
        console.error('Falha ao carregar histórico de privacidade:', error);
        appState.privacyEvents = [];
        renderPrivacyTrail();
    }
}

async function loadResidents() {
    try {
        const response = await fetch('/api/residents', {
            headers: {
                Accept: 'application/json',
            },
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        const residents = await response.json();
        appState.residents = Array.isArray(residents) ? residents : [];
        renderResidentPreview();
    } catch (error) {
        console.error('Falha ao carregar cadastros:', error);
        appState.residents = [];
        renderResidentPreview();
        setMessage(
            formStatus,
            'Não foi possível carregar os cadastros ativos no momento.',
            'error'
        );
    }
}

async function loadEmergencyAlerts() {
    try {
        const response = await fetch('/api/emergency-alerts', {
            headers: {
                Accept: 'application/json',
            },
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        const alerts = await response.json();
        appState.emergencyAlerts = Array.isArray(alerts) ? alerts : [];
        renderEmergencyAlerts();
    } catch (error) {
        console.error('Falha ao carregar alertas automáticos:', error);
        appState.emergencyAlerts = [];
        renderEmergencyAlerts();
    }
}

async function readApiError(response) {
    try {
        const payload = await response.json();

        if (payload && typeof payload.message === 'string') {
            return payload.message;
        }

        if (payload && payload.errors) {
            const messages = Object.values(payload.errors).flat();
            if (messages.length > 0) {
                return messages[0];
            }
        }

        if (payload && typeof payload.title === 'string') {
            return payload.title;
        }
    } catch (error) {
        console.error('Falha ao interpretar erro da API:', error);
    }

    return 'Falha de comunicação com o serviço.';
}

async function handleResidentSubmit(event) {
    event.preventDefault();

    if (!appState.faceEncodingReady) {
        setMessage(
            formStatus,
            'A confirmação facial ainda não está pronta. Aguarde o serviço ficar disponível.',
            'error'
        );
        return;
    }

    const isValid = await validateResidentFormLocally();

    if (!isValid) {
        return;
    }

    const formData = new FormData(residentForm);

    try {
        const response = await fetch('/api/residents', {
            method: 'POST',
            body: formData,
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        const resident = await response.json();
        suppressNextResetMessage = true;
        residentForm.reset();
        resetAddressLookupState('Informe um CEP de Teresópolis/RJ para iniciar a validação do endereço.');
        cityStateDisplay.value = 'Teresópolis / RJ';
        clearFieldValidity();
        await refreshPilotData();

        setMessage(
            formStatus,
            `${resident.name} foi cadastrado com endereço validado e protocolo automático de 48 horas ativo.`,
            'success'
        );

        setMessage(
            recognitionStatus,
            'Cadastro concluído. Abra a câmera para fazer a primeira confirmação facial de presença.',
            'neutral'
        );
    } catch (error) {
        console.error('Falha ao concluir o cadastro:', error);
        setMessage(
            formStatus,
            error instanceof Error ? error.message : 'Não foi possível concluir o cadastro.',
            'error'
        );
    }
}

function handleFormReset() {
    if (suppressNextResetMessage) {
        suppressNextResetMessage = false;
        return;
    }

    resetAddressLookupState('Informe um CEP de Teresópolis/RJ para iniciar a validação do endereço.');
    cityStateDisplay.value = 'Teresópolis / RJ';
    clearFieldValidity();

    window.requestAnimationFrame(() => {
        setMessage(
            formStatus,
            appState.apiOnline
                ? 'Formulário limpo. O protocolo de cuidado pode ser reiniciado a qualquer momento.'
                : 'Formulário limpo. O serviço ainda precisa estar online para concluir o cadastro.',
            'neutral'
        );
    });
}

function handleResidentInput(event) {
    const target = event.target;

    if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement)) {
        return;
    }

    switch (target.name) {
        case 'name':
        case 'emergencyContactName':
        case 'emergencyContactRelationship':
        case 'street':
        case 'neighborhood':
            target.value = normalizeSpaces(target.value);
            break;
        case 'cpf':
            target.value = formatCpf(target.value);
            break;
        case 'postalCode':
            target.value = formatPostalCode(target.value);
            if (appState.addressLookup?.postalCodeDigits !== normalizeDigits(target.value)) {
                resetAddressLookupState(
                    normalizeDigits(target.value).length === 8
                        ? 'Clique fora do campo para validar o CEP informado em Teresópolis/RJ.'
                        : 'Informe um CEP de Teresópolis/RJ para iniciar a validação do endereço.'
                );
            }
            break;
        case 'emergencyContactPhone':
            target.value = formatPhone(target.value);
            break;
        case 'emergencyContactEmail':
            target.value = normalizeEmail(target.value);
            break;
        default:
            break;
    }

    applyFieldValidation(target);
}

async function handleResidentFieldBlur(event) {
    const target = event.target;

    if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement)) {
        return;
    }

    if (target.name === 'postalCode') {
        if (normalizeDigits(target.value).length === 8) {
            await lookupAddressByPostalCode();
        } else {
            applyFieldValidation(target);
        }

        return;
    }

    applyFieldValidation(target);
}

async function validateResidentFormLocally() {
    applyAllFieldValidations();

    if (normalizeDigits(formFields.postalCode.value).length === 8 &&
        (!appState.addressLookup || appState.addressLookup.postalCodeDigits !== normalizeDigits(formFields.postalCode.value))) {
        await lookupAddressByPostalCode();
        applyAllFieldValidations();
    }

    if (!residentForm.checkValidity()) {
        residentForm.reportValidity();
        setMessage(formStatus, collectFirstValidationMessage(), 'error');
        return false;
    }

    return true;
}

function applyAllFieldValidations() {
    trackedFields.forEach((field) => applyFieldValidation(field));
}

function clearFieldValidity() {
    trackedFields.forEach((field) => {
        if (field) {
            field.setCustomValidity('');
        }
    });
}

function applyFieldValidation(field) {
    if (!field) {
        return;
    }

    const message = getValidationMessage(field);
    field.setCustomValidity(formatDisplayText(message));
}

function getValidationMessage(field) {
    switch (field.name) {
        case 'name':
            return getFullNameValidationMessage(field.value, 'Informe nome e sobrenome do titular.');
        case 'cpf':
            return getCpfValidationMessage(field.value);
        case 'postalCode':
            return getPostalCodeValidationMessage(field.value);
        case 'street':
            return getStreetValidationMessage(field.value);
        case 'number':
            return getNumberValidationMessage(field.value);
        case 'neighborhood':
            return getNeighborhoodValidationMessage(field.value);
        case 'emergencyContactName':
            return getFullNameValidationMessage(field.value, 'Informe nome e sobrenome do contato de emergência.');
        case 'emergencyContactPhone':
            return getEmergencyPhoneValidationMessage(field.value);
        case 'emergencyContactEmail':
            return getEmergencyEmailValidationMessage(field.value);
        case 'emergencyContactRelationship':
            return field.value.trim().length > 1 ? '' : 'Informe a relação do contato com você.';
        case 'alertDestination':
            return isValidAlertDestination(field.value) ? '' : 'Selecione quem deve ser acionado após 48 horas sem reconhecimento.';
        default:
            return '';
    }
}

function collectFirstValidationMessage() {
    for (const field of trackedFields) {
        if (field && field.validationMessage) {
            return field.validationMessage;
        }
    }

    return 'Revise os dados informados antes de concluir o cadastro.';
}

function getFullNameValidationMessage(value, fallbackMessage) {
    const parts = normalizeSpaces(value)
        .split(' ')
        .filter(Boolean)
        .filter((part) => /[A-Za-zÀ-ÿ]/.test(part));

    if (parts.length < 2) {
        return fallbackMessage;
    }

    if (countLetters(parts[0]) < 2 || countLetters(parts[parts.length - 1]) < 2) {
        return fallbackMessage;
    }

    return '';
}

function getCpfValidationMessage(value) {
    const digits = normalizeDigits(value);

    if (digits.length !== 11) {
        return 'Informe um CPF válido com 11 dígitos.';
    }

    if (!isValidCpf(value)) {
        return 'O CPF informado não passou na validação.';
    }

    return '';
}

function getPostalCodeValidationMessage(value) {
    const digits = normalizeDigits(value);

    if (digits.length !== 8) {
        return 'Informe um CEP válido com 8 dígitos.';
    }

    if (appState.addressLookupError) {
        return appState.addressLookupError;
    }

    if (!appState.addressLookup || appState.addressLookup.postalCodeDigits !== digits) {
        return 'Confirme um CEP válido em Teresópolis/RJ.';
    }

    return '';
}

function getStreetValidationMessage(value) {
    if (normalizeSpaces(value).length < 4) {
        return 'Informe o logradouro completo do titular.';
    }

    if (appState.addressLookup?.street && !matchesText(value, appState.addressLookup.street)) {
        return 'O logradouro deve corresponder ao CEP informado.';
    }

    return '';
}

function getNumberValidationMessage(value) {
    if (!normalizeDigits(value)) {
        return 'Informe o número do endereço.';
    }

    return '';
}

function getNeighborhoodValidationMessage(value) {
    if (normalizeSpaces(value).length < 3) {
        return 'Informe o bairro do titular.';
    }

    if (appState.addressLookup?.neighborhood && !matchesText(value, appState.addressLookup.neighborhood)) {
        return 'O bairro deve corresponder ao CEP informado.';
    }

    return '';
}

function getEmergencyPhoneValidationMessage(value) {
    const digits = normalizeDigits(value);

    return digits.length >= 10 && digits.length <= 11
        ? ''
        : 'Informe um telefone de emergência válido com DDD.';
}

function getEmergencyEmailValidationMessage(value) {
    const normalized = normalizeEmail(value);

    if (!normalized) {
        return '';
    }

    return isValidEmailAddress(normalized)
        ? ''
        : 'Informe um e-mail válido para o contato de emergência.';
}

async function lookupAddressByPostalCode() {
    const postalCodeDigits = normalizeDigits(formFields.postalCode.value);

    if (postalCodeDigits.length !== 8) {
        return false;
    }

    const requestId = ++appState.addressLookupToken;
    appState.addressLookupError = '';
    setMessage(addressStatus, 'Validando o CEP informado em Teresópolis/RJ...', 'neutral');

    try {
        const response = await fetch(`/api/address-lookup?postalCode=${encodeURIComponent(postalCodeDigits)}`, {
            headers: {
                Accept: 'application/json',
            },
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        const result = await response.json();

        if (requestId !== appState.addressLookupToken) {
            return false;
        }

        appState.addressLookup = {
            postalCodeDigits: result.postalCode,
            street: result.street || '',
            neighborhood: result.neighborhood || '',
            city: result.city || 'Teresópolis',
            state: result.state || 'RJ',
        };
        appState.addressLookupError = '';
        formFields.postalCode.value = formatPostalCode(result.postalCode);

        if (result.street) {
            formFields.street.value = result.street;
        }

        if (result.neighborhood) {
            formFields.neighborhood.value = result.neighborhood;
        }

        cityStateDisplay.value = `${result.city || 'Teresópolis'} / ${result.state || 'RJ'}`;
        setMessage(addressStatus, 'CEP confirmado em Teresópolis/RJ. Confira logradouro, número e bairro.', 'success');
        applyFieldValidation(formFields.postalCode);
        applyFieldValidation(formFields.street);
        applyFieldValidation(formFields.neighborhood);
        return true;
    } catch (error) {
        if (requestId !== appState.addressLookupToken) {
            return false;
        }

        appState.addressLookup = null;
    appState.addressLookupError = error instanceof Error ? error.message : 'Não foi possível validar o CEP informado.';
    cityStateDisplay.value = 'Teresópolis / RJ';
        setMessage(addressStatus, appState.addressLookupError, 'error');
        applyFieldValidation(formFields.postalCode);
        return false;
    }
}

function resetAddressLookupState(message) {
    appState.addressLookup = null;
    appState.addressLookupError = '';
    appState.addressLookupToken += 1;
    setMessage(addressStatus, message, 'neutral');
}

async function handleResidentActionClick(event) {
    const button = event.target.closest('button[data-resident-action]');

    if (!button) {
        return;
    }

    if (!appState.apiOnline) {
        setMessage(formStatus, 'O serviço precisa estar online para concluir alterações de privacidade.', 'error');
        return;
    }

    const residentId = button.dataset.residentId;
    const resident = appState.residents.find((item) => item.id === residentId);

    if (!resident) {
        setMessage(formStatus, 'Não foi possível localizar o cadastro selecionado.', 'error');
        return;
    }

    if (button.dataset.residentAction === 'revoke') {
        await revokeResident(resident);
        return;
    }

    if (button.dataset.residentAction === 'delete') {
        await deleteResident(resident);
    }
}

async function processPendingAlerts() {
    if (!appState.apiOnline) {
        setMessage(formStatus, 'O serviço precisa estar online para processar os acionamentos.', 'error');
        return;
    }

    processAlertsButton.disabled = true;

    try {
        const response = await fetch('/api/emergency-alerts/process', {
            method: 'POST',
            headers: {
                Accept: 'application/json',
            },
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        const result = await response.json();
        await refreshPilotData();

        if (!result.pendingAlerts) {
            setMessage(formStatus, 'Nenhum alerta pendente precisou de processamento neste momento.', 'neutral');
            return;
        }

        const tone = result.failedDeliveries > 0 ? 'neutral' : 'success';
        setMessage(
            formStatus,
            `Processamento concluído: ${result.pendingAlerts} alerta(s), ${result.successfulDeliveries} entrega(s) com sucesso e ${result.failedDeliveries} falha(s).`,
            tone
        );
    } catch (error) {
        console.error('Falha ao processar alertas pendentes:', error);
        setMessage(
            formStatus,
            error instanceof Error ? error.message : 'Não foi possível processar os alertas pendentes.',
            'error'
        );
    } finally {
        processAlertsButton.disabled = !appState.apiOnline;
    }
}

async function revokeResident(resident) {
    const reason = promptForReason(
        `Motivo da revogação para ${resident.name}:`,
        'Consentimento revogado pelo titular.'
    );

    if (reason === null) {
        return;
    }

    try {
        const response = await fetch(`/api/residents/${resident.id}/revoke`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                Accept: 'application/json',
            },
            body: JSON.stringify({ reason }),
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        await refreshPilotData();
        setMessage(
            formStatus,
            `${resident.name} teve o consentimento revogado. A biometria foi retirada do monitoramento.`,
            'success'
        );
    } catch (error) {
        console.error('Falha ao revogar cadastro:', error);
        setMessage(
            formStatus,
            error instanceof Error ? error.message : 'Não foi possível revogar o cadastro selecionado.',
            'error'
        );
    }
}

async function deleteResident(resident) {
    const confirmed = window.confirm(
        `Excluir definitivamente o cadastro de ${resident.name}? Esta ação remove o acompanhamento ativo e preserva apenas o histórico de privacidade.`
    );

    if (!confirmed) {
        return;
    }

    const reason = promptForReason(
        `Motivo da exclusão para ${resident.name}:`,
        'Exclusão solicitada pelo titular.'
    );

    if (reason === null) {
        return;
    }

    try {
        const response = await fetch(`/api/residents/${resident.id}`, {
            method: 'DELETE',
            headers: {
                'Content-Type': 'application/json',
                Accept: 'application/json',
            },
            body: JSON.stringify({ reason }),
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        await refreshPilotData();
        setMessage(
            formStatus,
            `${resident.name} foi excluído do acompanhamento e o histórico de privacidade foi atualizado.`,
            'success'
        );
    } catch (error) {
        console.error('Falha ao excluir cadastro:', error);
        setMessage(
            formStatus,
            error instanceof Error ? error.message : 'Não foi possível excluir o cadastro selecionado.',
            'error'
        );
    }
}

function promptForReason(promptText, fallbackValue) {
    const reason = window.prompt(formatDisplayText(promptText), formatDisplayText(fallbackValue));

    if (reason === null) {
        return null;
    }

    return reason.trim() || fallbackValue;
}

async function startCamera() {
    if (appState.stream) {
        setMessage(recognitionStatus, 'A câmera já está ativa neste navegador.', 'neutral');
        return;
    }

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        setMessage(recognitionStatus, 'Este navegador não suporta acesso à câmera.', 'error');
        return;
    }

    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: 'user',
            },
            audio: false,
        });

        appState.stream = stream;
        video.srcObject = stream;
        await video.play();
        cameraState.textContent = 'online';
        cameraState.classList.add('live');

        setMessage(
            recognitionStatus,
            'Câmera ativa. A próxima verificação facial vai renovar sua janela de 48 horas.',
            'success'
        );
    } catch (error) {
        console.error('Falha ao acessar câmera:', error);
        setMessage(
            recognitionStatus,
            'Não foi possível acessar a câmera. Verifique as permissões do navegador.',
            'error'
        );
    }
}

function stopCamera() {
    if (!appState.stream) {
        return;
    }

    appState.stream.getTracks().forEach((track) => track.stop());
    appState.stream = null;
    video.srcObject = null;
    cameraState.textContent = 'offline';
    cameraState.classList.remove('live');

    setMessage(
        recognitionStatus,
        'Câmera encerrada. O acompanhamento continua ativo com base na sua última confirmação.',
        'neutral'
    );
}

async function simulateRecognition() {
    if (!appState.faceEncodingReady) {
        setMessage(
            recognitionStatus,
            'A confirmação facial ainda não está pronta no serviço.',
            'error'
        );
        return;
    }

    const activeResidents = appState.residents.filter(
        (resident) => resident.status === 'ativo' && resident.biometricReady
    );

    if (activeResidents.length === 0) {
        setMessage(
            recognitionStatus,
            'Não existe cadastro ativo com biometria pronta para confirmação.',
            'error'
        );
        return;
    }

    if (!appState.stream) {
        setMessage(
            recognitionStatus,
            'Abra a câmera para enviar uma nova confirmação facial.',
            'error'
        );
        return;
    }

    try {
        const captureBlob = await captureFrameFromVideo();
        const formData = new FormData();
        formData.append('photo', captureBlob, `capture-${Date.now()}.jpg`);

        const response = await fetch('/api/recognitions/verify', {
            method: 'POST',
            body: formData,
        });

        if (!response.ok) {
            throw new Error(await readApiError(response));
        }

        const result = await response.json();

        if (result.matchFound) {
            await refreshPilotData();
            setMessage(
                recognitionStatus,
                `Presença confirmada para ${result.name}. A janela de monitoramento foi renovada com confiança de ${result.confidence}%.`,
                'success'
            );
            return;
        }

        setMessage(
            recognitionStatus,
            `${result.message} Faces detectadas na captura: ${result.facesDetected}.`,
            'neutral'
        );
    } catch (error) {
        console.error('Falha na verificação facial:', error);
        setMessage(
            recognitionStatus,
            error instanceof Error ? error.message : 'Não foi possível concluir a verificação facial.',
            'error'
        );
    }
}

function captureFrameFromVideo() {
    return new Promise((resolve, reject) => {
        if (!video.videoWidth || !video.videoHeight) {
            reject(new Error('A câmera ainda não entregou um frame válido para comparação.'));
            return;
        }

        const canvas = document.createElement('canvas');
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;

        const context = canvas.getContext('2d');

        if (!context) {
            reject(new Error('Não foi possível preparar a captura da câmera.'));
            return;
        }

        context.drawImage(video, 0, 0, canvas.width, canvas.height);
        canvas.toBlob((blob) => {
            if (!blob) {
                reject(new Error('Não foi possível converter a captura em imagem.'));
                return;
            }

            resolve(blob);
        }, 'image/jpeg', 0.92);
    });
}

function createPreviewRow(label, value) {
    const row = document.createElement('div');
    row.className = 'preview-row';

    const labelElement = document.createElement('span');
    labelElement.className = 'preview-label';
    labelElement.textContent = formatDisplayText(label);

    const valueElement = document.createElement('strong');
    valueElement.textContent = typeof value === 'string' ? formatDisplayText(value) : value;

    row.append(labelElement, valueElement);

    return row;
}

function createResidentActionButton(label, tone, action, residentId) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `button ${tone} resident-action-button`;
    button.textContent = formatDisplayText(label);
    button.dataset.residentAction = action;
    button.dataset.residentId = residentId;
    button.disabled = !appState.apiOnline;
    return button;
}

function createResidentCard(resident) {
    const item = document.createElement('article');
    item.className = 'resident-item';

    const header = document.createElement('div');
    header.className = 'resident-item-header';

    const titleBlock = document.createElement('div');
    const title = document.createElement('h4');
    title.textContent = resident.name;

    const protocolTag = document.createElement('span');
    protocolTag.className = 'preview-token';
    protocolTag.textContent = resident.requiresEmergencyAlert ? 'alerta 48h pendente' : 'monitoramento 48h ativo';

    titleBlock.append(title, protocolTag);

    const status = document.createElement('span');
    status.className = `status-pill resident-status ${resolveResidentStatusTone(resident)}`;
    status.textContent = resolveResidentStatusLabel(resident);

    header.append(titleBlock, status);

    const meta = document.createElement('div');
    meta.className = 'resident-meta';
    meta.append(
        createPreviewRow('CPF mascarado', resident.cpfMasked),
        createPreviewRow('Endereço', resident.address),
        createPreviewRow('Contato de emergência', `${resident.emergencyContactName} (${resident.emergencyContactRelationship})`),
        createPreviewRow('Telefone do contato', resident.emergencyContactPhoneMasked || 'Não informado'),
        createPreviewRow('E-mail do contato', resident.emergencyContactEmailMasked || 'Não informado'),
        createPreviewRow('Acionamento em ausência', formatAlertDestination(resident.alertDestination)),
        createPreviewRow('Última confirmação facial', resident.lastRecognitionAt ? formatDisplayDate(resident.lastRecognitionAt) : 'Ainda não realizada'),
        createPreviewRow('Último alerta automático', resident.lastEmergencyAlertAt ? formatDisplayDate(resident.lastEmergencyAlertAt) : 'Nenhum alerta até agora'),
        createPreviewRow('Biometria', resident.biometricReady ? 'ativa para confirmação' : 'removida ou indisponível')
    );

    if (resident.revokedAt) {
        meta.append(createPreviewRow('Revogado em', formatDisplayDate(resident.revokedAt)));
    }

    if (resident.revocationReason) {
        meta.append(createPreviewRow('Motivo da revogação', resident.revocationReason));
    }

    const actions = document.createElement('div');
    actions.className = 'resident-actions';

    if (resident.status !== 'revogado') {
        actions.append(createResidentActionButton('Revogar consentimento', 'secondary', 'revoke', resident.id));
    }

    actions.append(createResidentActionButton('Excluir cadastro', 'ghost', 'delete', resident.id));

    item.append(header, meta, actions);

    return item;
}

function renderResidentPreview() {
    const totalResidents = appState.residents.length;
    const activeResidents = appState.residents.filter((resident) => resident.status === 'ativo').length;
    residentCount.textContent = `${totalResidents} cadastros / ${activeResidents} ativos`;

    residentPreview.replaceChildren();

    if (totalResidents === 0) {
        residentPreview.classList.add('empty');

        const emptyMessage = document.createElement('p');
        emptyMessage.textContent = 'Nenhum cadastro ativo ainda.';
        residentPreview.append(emptyMessage);
        return;
    }

    residentPreview.classList.remove('empty');

    const list = document.createElement('div');
    list.className = 'resident-list';

    [...appState.residents]
        .sort((left, right) => new Date(right.createdAt) - new Date(left.createdAt))
        .forEach((resident) => {
            list.append(createResidentCard(resident));
        });

    residentPreview.append(list);
}

function createEmergencyAlertItem(alert) {
    const item = document.createElement('article');
    item.className = 'audit-item';

    const header = document.createElement('div');
    header.className = 'audit-item-header';

    const title = document.createElement('h4');
    title.textContent = alert.residentName;

    const status = document.createElement('span');
    status.className = `status-pill ${resolveAlertStatusTone(alert.status)}`;
    status.textContent = formatDisplayText(`${resolveAlertStatusLabel(alert.status)} · ${alert.hoursWithoutRecognition}h`);

    header.append(title, status);

    const description = document.createElement('p');
    description.textContent = formatDisplayText(`${alert.destinationDescription}. Última referência em ${formatDisplayDate(alert.referenceTime)}.`);

    const deliverySummary = document.createElement('p');
    deliverySummary.className = 'alert-delivery-summary';
    deliverySummary.textContent = formatDisplayText(alert.dispatchSummary || 'Despacho ainda não executado para este alerta.');

    const meta = document.createElement('div');
    meta.className = 'audit-meta';

    const chips = [
        `CPF ${alert.cpfMasked}`,
        alert.address,
        `${alert.contactName} / ${alert.contactRelationship}`,
        alert.contactPhoneMasked,
        alert.contactEmailMasked,
        `Registrado em ${formatDisplayDate(alert.triggeredAt)}`,
        `${alert.successfulDeliveries} entrega(s) concluidas`,
        `${alert.failedDeliveries} falha(s)`,
    ];

    if (alert.lastDispatchAttemptAt) {
        chips.push(`Última tentativa em ${formatDisplayDate(alert.lastDispatchAttemptAt)}`);
    }

    if (alert.dispatchedAt) {
        chips.push(`Acionado em ${formatDisplayDate(alert.dispatchedAt)}`);
    }

    chips.forEach((label) => {
        const chip = document.createElement('span');
        chip.textContent = formatDisplayText(label);
        meta.append(chip);
    });

    item.append(header, description, deliverySummary, meta);

    return item;
}

function renderEmergencyAlerts() {
    emergencyAlertCount.textContent = `${appState.emergencyAlerts.length} alertas`;
    emergencyAlertsPanel.replaceChildren();

    if (appState.emergencyAlerts.length === 0) {
        emergencyAlertsPanel.classList.add('empty');

        const emptyMessage = document.createElement('p');
        emptyMessage.textContent = 'Nenhum alerta automático registrado.';
        emergencyAlertsPanel.append(emptyMessage);
        return;
    }

    emergencyAlertsPanel.classList.remove('empty');

    const list = document.createElement('div');
    list.className = 'audit-events';

    [...appState.emergencyAlerts]
        .sort((left, right) => new Date(right.triggeredAt) - new Date(left.triggeredAt))
        .forEach((alert) => {
            list.append(createEmergencyAlertItem(alert));
        });

    emergencyAlertsPanel.append(list);
}

function createAuditItem(event) {
    const item = document.createElement('article');
    item.className = 'audit-item';

    const header = document.createElement('div');
    header.className = 'audit-item-header';

    const title = document.createElement('h4');
    title.textContent = event.eventType === 'consent_revoked' ? 'Consentimento revogado' : 'Cadastro excluído';

    const status = document.createElement('span');
    status.className = 'status-pill warning';
    status.textContent = formatDisplayText(event.statusAfterAction);

    header.append(title, status);

    const description = document.createElement('p');
    description.textContent = formatDisplayText(event.reason);

    const meta = document.createElement('div');
    meta.className = 'audit-meta';

    const chips = [
        `CPF ${event.cpfMasked}`,
        `Em ${formatDisplayDate(event.occurredAt)}`,
        event.photoDeleted ? 'foto removida' : 'foto não removida',
        event.faceEncodingDeleted ? 'biometria removida' : 'biometria não removida',
    ];

    chips.forEach((label) => {
        const chip = document.createElement('span');
        chip.textContent = formatDisplayText(label);
        meta.append(chip);
    });

    item.append(header, description, meta);

    return item;
}

function renderPrivacyTrail() {
    auditCount.textContent = `${appState.privacyEvents.length} eventos`;
    privacyTrail.replaceChildren();

    if (appState.privacyEvents.length === 0) {
        privacyTrail.classList.add('empty');

        const emptyMessage = document.createElement('p');
        emptyMessage.textContent = 'Nenhum evento de privacidade registrado ainda.';
        privacyTrail.append(emptyMessage);
        return;
    }

    privacyTrail.classList.remove('empty');

    const list = document.createElement('div');
    list.className = 'audit-events';

    [...appState.privacyEvents]
        .sort((left, right) => new Date(right.occurredAt) - new Date(left.occurredAt))
        .forEach((event) => {
            list.append(createAuditItem(event));
        });

    privacyTrail.append(list);
}

function resolveResidentStatusLabel(resident) {
    if (resident.status === 'revogado') {
        return 'revogado';
    }

    return resident.requiresEmergencyAlert ? 'alerta 48h' : 'ativo';
}

function resolveResidentStatusTone(resident) {
    if (resident.status === 'revogado') {
        return 'revoked';
    }

    return resident.requiresEmergencyAlert ? 'warning' : 'live';
}

function resolveAlertStatusLabel(status) {
    switch (status) {
        case 'acionado_com_sucesso':
            return 'acionado';
        case 'acionamento_parcial':
            return 'acionamento parcial';
        case 'falha_no_acionamento':
            return 'falha no envio';
        case 'nenhum_canal_configurado':
            return 'sem canal configurado';
        default:
            return 'envio pendente';
    }
}

function resolveAlertStatusTone(status) {
    switch (status) {
        case 'acionado_com_sucesso':
            return 'live';
        case 'acionamento_parcial':
        case 'acionamento_automatico_pendente':
            return 'warning';
        default:
            return 'revoked';
    }
}

function formatAlertDestination(destination) {
    return destination === 'contato_e_orgao_publico'
    ? 'Contato de emergência e órgão público de apoio'
    : 'Somente contato de emergência';
}

function formatDisplayDate(value) {
    const date = new Date(value);

    if (Number.isNaN(date.getTime())) {
        return value;
    }

    return date.toLocaleString('pt-BR');
}

function normalizeSpaces(value) {
    return value.replace(/\s+/g, ' ').trimStart();
}

function normalizeDigits(value) {
    return String(value || '').replace(/\D/g, '');
}

function normalizeEmail(value) {
    return String(value || '').trim().replace(/\s+/g, '').toLowerCase();
}

function countLetters(value) {
    return Array.from(value).filter((character) => /[A-Za-zÀ-ÿ]/.test(character)).length;
}

function isValidCpf(value) {
    const digits = normalizeDigits(value);

    if (digits.length !== 11 || new Set(digits).size === 1) {
        return false;
    }

    const firstDigit = calculateCpfDigit(digits.slice(0, 9), 10);
    const secondDigit = calculateCpfDigit(digits.slice(0, 10), 11);

    return Number(digits[9]) === firstDigit && Number(digits[10]) === secondDigit;
}

function calculateCpfDigit(source, weight) {
    let sum = 0;

    for (let index = 0; index < source.length; index += 1) {
        sum += Number(source[index]) * (weight - index);
    }

    const remainder = sum % 11;
    return remainder < 2 ? 0 : 11 - remainder;
}

function formatCpf(value) {
    const digits = normalizeDigits(value).slice(0, 11);

    if (digits.length <= 3) {
        return digits;
    }

    if (digits.length <= 6) {
        return `${digits.slice(0, 3)}.${digits.slice(3)}`;
    }

    if (digits.length <= 9) {
        return `${digits.slice(0, 3)}.${digits.slice(3, 6)}.${digits.slice(6)}`;
    }

    return `${digits.slice(0, 3)}.${digits.slice(3, 6)}.${digits.slice(6, 9)}-${digits.slice(9)}`;
}

function formatPostalCode(value) {
    const digits = normalizeDigits(value).slice(0, 8);

    if (digits.length <= 5) {
        return digits;
    }

    return `${digits.slice(0, 5)}-${digits.slice(5)}`;
}

function formatPhone(value) {
    const digits = normalizeDigits(value).slice(0, 11);

    if (digits.length <= 2) {
        return digits.length === 0 ? '' : `(${digits}`;
    }

    if (digits.length <= 6) {
        return `(${digits.slice(0, 2)}) ${digits.slice(2)}`;
    }

    if (digits.length <= 10) {
        return `(${digits.slice(0, 2)}) ${digits.slice(2, 6)}-${digits.slice(6)}`;
    }

    return `(${digits.slice(0, 2)}) ${digits.slice(2, 7)}-${digits.slice(7)}`;
}

function normalizeText(value) {
    return String(value || '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/[^A-Za-z0-9]+/g, ' ')
        .trim()
        .toUpperCase();
}

function matchesText(left, right) {
    const leftNormalized = normalizeText(left);
    const rightNormalized = normalizeText(right);

    if (!leftNormalized || !rightNormalized) {
        return false;
    }

    return leftNormalized === rightNormalized ||
        leftNormalized.includes(rightNormalized) ||
        rightNormalized.includes(leftNormalized);
}

function isValidAlertDestination(value) {
    return value === 'contato_emergencia' || value === 'contato_e_orgao_publico';
}

function isValidEmailAddress(value) {
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(String(value || '').trim());
}
