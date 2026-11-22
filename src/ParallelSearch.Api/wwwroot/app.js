const elements = {
  healthButton: document.querySelector('#health-button'),
  apiStatus: document.querySelector('#api-status'),
  ollamaStatus: document.querySelector('#ollama-status'),
  message: document.querySelector('#message'),
  indexForm: document.querySelector('#index-form'),
  indexButton: document.querySelector('#index-button'),
  threads: document.querySelector('#threads'),
  documents: document.querySelector('#documents'),
  buildTime: document.querySelector('#build-time'),
  threadsUsed: document.querySelector('#threads-used'),
  searchForm: document.querySelector('#search-form'),
  searchButton: document.querySelector('#search-button'),
  query: document.querySelector('#query'),
  expansionStage: document.querySelector('#expansion-stage'),
  expansionText: document.querySelector('#expansion-text'),
  searchStage: document.querySelector('#search-stage'),
  searchText: document.querySelector('#search-text'),
  doneStage: document.querySelector('#done-stage'),
  doneText: document.querySelector('#done-text'),
  resultSummary: document.querySelector('#result-summary'),
  results: document.querySelector('#results')
};

async function requestJson(url, options) {
  const response = await fetch(url, options);
  const data = await response.json().catch(() => ({}));

  if (!response.ok) {
    throw new Error(data.error || `HTTP ${response.status}`);
  }

  return data;
}

function setStatus(element, text, kind) {
  element.textContent = text;
  element.className = `status ${kind}`;
}

function showMessage(text, isError = false) {
  elements.message.textContent = text;
  elements.message.style.color = isError ? 'var(--danger)' : '';
}

async function checkHealth() {
  elements.healthButton.disabled = true;
  showMessage('Перевіряємо сервіси…');

  try {
    const health = await requestJson('/api/health');
    setStatus(elements.apiStatus, `API: ${health.status}`, 'success');
    setStatus(
      elements.ollamaStatus,
      `Ollama: ${health.ollama ? 'доступна' : 'недоступна'}`,
      health.ollama ? 'success' : 'warning');
    showMessage(health.ollama
      ? 'Усі сервіси доступні.'
      : 'Пошук працюватиме з початковим запитом без розширення LLM.');
  } catch (error) {
    setStatus(elements.apiStatus, 'API: недоступний', 'error');
    setStatus(elements.ollamaStatus, 'Ollama: невідомо', 'neutral');
    showMessage(error.message, true);
  } finally {
    elements.healthButton.disabled = false;
  }
}

async function refreshStats() {
  try {
    const stats = await requestJson('/api/stats');
    elements.documents.textContent = stats.documents.toLocaleString();
    elements.buildTime.textContent = `${formatNumber(stats.index_build_time_ms)} ms`;
    elements.threadsUsed.textContent = stats.threads_used;
  } catch (error) {
    showMessage(`Не вдалося отримати статистику: ${error.message}`, true);
  }
}

elements.indexForm.addEventListener('submit', async event => {
  event.preventDefault();
  const threads = Number(elements.threads.value);

  if (!Number.isInteger(threads) || threads <= 0) {
    showMessage('Кількість потоків має бути додатним цілим числом.', true);
    return;
  }

  elements.indexButton.disabled = true;
  showMessage('Триває паралельна індексація…');

  try {
    const result = await requestJson('/api/index', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ threads })
    });
    showMessage(
      `Проіндексовано ${result.documents_indexed.toLocaleString()} документів за ${formatNumber(result.time_ms)} ms.`);
    await refreshStats();
  } catch (error) {
    showMessage(`Індексація не виконана: ${error.message}`, true);
  } finally {
    elements.indexButton.disabled = false;
  }
});

elements.searchForm.addEventListener('submit', event => {
  event.preventDefault();
  const query = elements.query.value.trim();

  if (!query) {
    showMessage('Введіть пошуковий запит.', true);
    return;
  }

  resetSearchStages();
  elements.searchButton.disabled = true;
  elements.expansionStage.classList.add('active');
  elements.expansionText.textContent = 'Ollama розширює запит…';
  elements.results.replaceChildren();
  elements.resultSummary.textContent = 'Пошук виконується…';

  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const socket = new WebSocket(`${protocol}//${location.host}/ws/search`);
  let completed = false;
  let reportedError = false;

  socket.addEventListener('open', () => {
    socket.send(JSON.stringify({ query }));
  });

  socket.addEventListener('message', event => {
    let message;

    try {
      message = JSON.parse(event.data);
    } catch {
      reportedError = true;
      showMessage('Сервер повернув некоректне WebSocket-повідомлення.', true);
      return;
    }

    if (message.stage === 'query_expansion') {
      const terms = message.data.expanded_terms || [];
      elements.expansionStage.classList.remove('active');
      elements.expansionStage.classList.add(message.data.error ? 'warning' : 'success');
      elements.expansionText.textContent = message.data.error
        ? `Ollama недоступна: ${message.data.error}. Використовується початковий запит.`
        : terms.length
          ? `Додаткові терміни: ${terms.join(', ')}`
          : 'Додаткових термінів немає.';
      elements.searchStage.classList.add('active');
      elements.searchText.textContent = 'Пошук за інвертованим індексом…';
    } else if (message.stage === 'keyword_search') {
      elements.searchStage.classList.remove('active');
      elements.searchStage.classList.add('success');
      elements.searchText.textContent = `Завершено за ${formatNumber(message.data.time_ms)} ms.`;
      renderResults(message.data.results || [], message.data.time_ms);
      elements.doneStage.classList.add('active');
      elements.doneText.textContent = 'Завершення запиту…';
    } else if (message.stage === 'done') {
      completed = true;
      elements.doneStage.classList.remove('active');
      elements.doneStage.classList.add('success');
      elements.doneText.textContent = 'Готово.';
      showMessage('Пошук завершено.');
    } else if (message.stage === 'error') {
      reportedError = true;
      showMessage(message.error || 'Помилка WebSocket-пошуку.', true);
    }
  });

  socket.addEventListener('error', () => {
    reportedError = true;
    showMessage('Не вдалося встановити WebSocket-з’єднання.', true);
  });

  socket.addEventListener('close', () => {
    elements.searchButton.disabled = false;
    if (!completed && !reportedError) {
      showMessage('WebSocket-з’єднання завершилося до закінчення пошуку.', true);
    }
  });
});

function resetSearchStages() {
  for (const stage of [
    elements.expansionStage,
    elements.searchStage,
    elements.doneStage
  ]) {
    stage.classList.remove('active', 'success', 'warning');
  }

  elements.expansionText.textContent = 'Очікує запуску';
  elements.searchText.textContent = 'Очікує запуску';
  elements.doneText.textContent = 'Очікує запуску';
}

function renderResults(results, timeMs) {
  elements.results.replaceChildren();
  elements.resultSummary.textContent =
    `${results.length.toLocaleString()} результатів · ${formatNumber(timeMs)} ms`;

  if (results.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = 'Документів за цим запитом не знайдено.';
    elements.results.append(empty);
    return;
  }

  for (const result of results) {
    const article = document.createElement('article');
    article.className = 'result';
    const header = document.createElement('div');
    header.className = 'result-header';
    const documentId = document.createElement('span');
    documentId.className = 'result-id';
    documentId.textContent = result.doc_id;
    const score = document.createElement('span');
    score.className = 'score';
    score.textContent = `score ${formatNumber(result.score)}`;
    const snippet = document.createElement('p');
    snippet.className = 'snippet';
    snippet.textContent = result.snippet || 'Фрагмент відсутній.';
    header.append(documentId, score);
    article.append(header, snippet);
    elements.results.append(article);
  }
}

function formatNumber(value) {
  return Number(value).toLocaleString(undefined, { maximumFractionDigits: 3 });
}

elements.healthButton.addEventListener('click', checkHealth);
checkHealth();
refreshStats();
