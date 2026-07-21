# SETT Memory Cleaner ⚡

Otimizador de RAM portátil. Usa **APIs nativas do Windows** para limpar memória de forma segura. Interface escura, executável único, sem instalação.

> ⚠️ **Requisito:** Requer privilégios de Administrador.

---

## ✨ Recursos

- **Auto-otimização:** Baseado em tempo ou uso.
- **Atalho Global:** `CTRL + SHIFT + M` para limpar rápido.
- **Modo Compacto:** UI minimalista.
- **Início Automático:** Configuração via sistema.
- **Exclusões:** Lista para ignorar processos.
- **System Tray:** Monitoramento na bandeja.
- **Bilíngue:** EN + PT-BR.

---

## 🧬 Como Funciona

Usa chamadas documentadas da **Windows API**. Sem truques.

| Área | Descrição |
| :--- | :--- |
| Combined Page List | Mesclagem páginas |
| Modified File Cache | Cache disco |
| Modified Page List | Páginas não salvas |
| Registry Cache | Cache registro |
| Standby List | Apps fechados |
| System File Cache | Arquivos sistema |
| Working Set | RAM de processos |

---

## 🔎 Verificação

Teste via **Monitor de Recursos** (`resmon.exe`):
1. Abra `resmon.exe` → aba **Memória**.
2. Veja barra **Standby**.
3. No SETT, clique **Optimize**.
4. Veja **Standby** cair, **Livre** subir.

---

## ⚠️ Problemas Comuns

**Antivírus detectou malware?**
Falso positivo comum. App acessa APIs de baixo nível e cria tarefas agendadas. Código é 100% aberto e seguro. Submeta para análise [Microsoft WDSI](https://www.microsoft.com/en-us/wdsi/filesubmission).

---

## 🔒 Segurança

- **Código Aberto:** GPL-3.0.
- **Sem Dependências:** Portátil.
- **Transparência:** Apenas chamadas oficiais.

---

## 📄 Licença

GPL-3.0. Veja [LICENSE](/LICENSE).
