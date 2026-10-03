/**
 * pixaitagger.js
 * Frontend controller for SwarmUI PixAI Tagger v1.0 extension.
 * Provides GPU-accelerated image tagging, drag-and-drop studio, viewer button, and prompt insertion.
 */

'use strict';

class PixaiTaggerHelper {
    constructor() {
        this.defaults = {
            general: 0.17,
            character: 0.27,
            clothing: 0.17,
            style: 0.15,
            copyright: 0.24
        };

        this.thresholds = Object.assign({}, this.defaults);
        this.includeConfidence = false;
        this.keepUnderscores = false;
        this.categories = {
            general: true,
            character: true,
            clothing: true,
            style: false,
            copyright: false
        };

        this.currentImageBase64 = null;
        this.modalElement = null;
        this.isProcessing = false;
        this.lastResult = null;
    }

    /**
     * Converts an image source URL or data URI into base64 string.
     * @param {string} src - The image URL or data-URL.
     */
    async getImageBase64(src) {
        if (!src) {
            return null;
        }
        if (src.startsWith('data:')) {
            let parts = src.split(',');
            return parts[1] || null;
        }
        let fetchResponse = await fetch(src);
        let blob = await fetchResponse.blob();
        return new Promise((resolve, reject) => {
            let reader = new FileReader();
            reader.onloadend = () => {
                let res = reader.result;
                let b64 = res.split(',')[1];
                resolve(b64 || null);
            };
            reader.onerror = () => {
                reject(new Error('Failed to read image data.'));
            };
            reader.readAsDataURL(blob);
        });
    }

    /**
     * Calls the PixAITaggerGenerateTags API on the SwarmUI server.
     * @param {string} base64Data - Base64 encoded image string.
     */
    async executeTagging(base64Data) {
        let payload = {
            imageBase64: base64Data,
            generalThreshold: this.thresholds.general,
            characterThreshold: this.thresholds.character,
            clothingThreshold: this.thresholds.clothing,
            styleThreshold: this.thresholds.style,
            copyrightThreshold: this.thresholds.copyright,
            enableGeneral: this.categories.general,
            enableCharacter: this.categories.character,
            enableClothing: this.categories.clothing,
            enableStyle: this.categories.style,
            enableCopyright: this.categories.copyright,
            includeConfidence: this.includeConfidence,
            keepUnderscores: this.keepUnderscores,
            filterTags: this.getParamValue('pixaifiltertags', '')
        };

        return new Promise((resolve, reject) => {
            genericRequest('PixaiTaggerGenerateTags', payload, (data) => {
                if (data && data.success) {
                    resolve(data);
                }
                else {
                    reject(new Error((data && data.error) ? data.error : 'Tagging failed on GPU.'));
                }
            });
        });
    }

    /**
     * Reads a parameter value from the T2I input elements if available.
     * @param {string} paramId - Parameter identifier.
     * @param {any} fallback - Default fallback value.
     */
    getParamValue(paramId, fallback) {
        let elem = document.getElementById('input_' + paramId);
        if (elem) {
            return elem.value;
        }
        return fallback;
    }

    /**
     * Synchronizes threshold values and toggle states from the T2I parameter controls if they exist.
     */
    syncFromT2IParams() {
        let parse = (id, fallback) => {
            let elem = document.getElementById('input_' + id);
            if (elem) {
                let val = parseFloat(elem.value);
                if (!isNaN(val)) {
                    return val;
                }
            }
            return fallback;
        };

        let parseToggled = (id, fallback) => {
            let toggle = document.getElementById('input_' + id + '_toggle');
            if (toggle) {
                return toggle.checked;
            }
            return fallback;
        };

        this.thresholds.general = parse('pixaigeneralthreshold', this.defaults.general);
        this.thresholds.character = parse('pixaicharacterthreshold', this.defaults.character);
        this.thresholds.clothing = parse('pixaiclothingthreshold', this.defaults.clothing);
        this.thresholds.style = parse('pixaistylethreshold', this.defaults.style);
        this.thresholds.copyright = parse('pixaicopyrightseriesthreshold', this.defaults.copyright);

        this.categories.general = parseToggled('pixaigeneralthreshold', this.categories.general);
        this.categories.character = parseToggled('pixaicharacterthreshold', this.categories.character);
        this.categories.clothing = parseToggled('pixaiclothingthreshold', this.categories.clothing);
        this.categories.style = parseToggled('pixaistylethreshold', this.categories.style);
        this.categories.copyright = parseToggled('pixaicopyrightseriesthreshold', this.categories.copyright);

        let confElem = document.getElementById('input_pixaiincludeconfidence');
        if (confElem) {
            this.includeConfidence = confElem.checked;
        }
        let underElem = document.getElementById('input_pixaikeepunderscores');
        if (underElem) {
            this.keepUnderscores = underElem.checked;
        }
    }

    /**
     * Synchronizes threshold values and toggle states to the T2I parameter controls if they exist.
     */
    syncToT2IParams() {
        let set = (id, val) => {
            let elem = document.getElementById('input_' + id);
            if (elem) {
                elem.value = val;
                triggerChangeFor(elem);
            }
        };

        let setToggled = (id, toggled) => {
            let toggle = document.getElementById('input_' + id + '_toggle');
            if (toggle && toggle.checked != toggled) {
                toggle.checked = toggled;
                localStorage.setItem('pixai_toggle_' + id, toggled ? 'true' : 'false');
                if (typeof doToggleEnable == 'function') {
                    doToggleEnable('input_' + id);
                }
                else {
                    triggerChangeFor(toggle);
                }
            }
        };

        set('pixaigeneralthreshold', this.thresholds.general);
        set('pixaicharacterthreshold', this.thresholds.character);
        set('pixaiclothingthreshold', this.thresholds.clothing);
        set('pixaistylethreshold', this.thresholds.style);
        set('pixaicopyrightseriesthreshold', this.thresholds.copyright);

        setToggled('pixaigeneralthreshold', this.categories.general);
        setToggled('pixaicharacterthreshold', this.categories.character);
        setToggled('pixaiclothingthreshold', this.categories.clothing);
        setToggled('pixaistylethreshold', this.categories.style);
        setToggled('pixaicopyrightseriesthreshold', this.categories.copyright);

        let confElem = document.getElementById('input_pixaiincludeconfidence');
        if (confElem) {
            confElem.checked = this.includeConfidence;
            triggerChangeFor(confElem);
        }
        let underElem = document.getElementById('input_pixaikeepunderscores');
        if (underElem) {
            underElem.checked = this.keepUnderscores;
            triggerChangeFor(underElem);
        }
    }

    /**
     * Handles clicking the "Generate PixAI Tags" media button on an image in the viewer.
     * @param {string} src - The image URL/data URI from registerMediaButton.
     */
    async handleViewerTag(src) {
        let base64Data = null;
        let didStartGen = false;
        try {
            base64Data = await this.getImageBase64(src);
        }
        catch (err) {
            showError('PixAI Tagger: Failed to read image data: ' + err.message);
            return;
        }
        if (!base64Data) {
            showError('PixAI Tagger: No image available in viewer to tag.');
            return;
        }

        this.syncFromT2IParams();

        try {
            if (typeof updateGenCount == 'function') {
                didStartGen = true;
                updateGenCount();
            }

            let result = await this.executeTagging(base64Data);
            if (!result || !result.tags) {
                showError('PixAI Tagger: No tags met the threshold criteria.');
                return;
            }

            this.insertTagsIntoPrompt(result.tags);
        }
        catch (err) {
            showError('PixAI Tagger GPU execution error: ' + err.message);
        }
        finally {
            if (didStartGen && typeof updateGenCount == 'function') {
                updateGenCount();
            }
        }
    }

    /**
     * Inserts tags into the main prompt box according to insert mode.
     * @param {string} tags - The generated tags string.
     * @param {string} modeOverride - Optional mode override ('replace', 'prepend', 'append').
     */
    insertTagsIntoPrompt(tags, modeOverride = null) {
        let promptBox = document.getElementById('alt_prompt_textbox');
        if (!promptBox) {
            return;
        }
        let insertMode = modeOverride || this.getParamValue('pixaiinsertmode', 'replace');
        let existing = promptBox.value;
        let promptTagMatch = existing.match(/<pixaitagger(?::([^>]+))?>/i);

        if (promptTagMatch) {
            promptBox.value = existing.replace(promptTagMatch[0], tags);
        }
        else if (insertMode == 'prepend') {
            promptBox.value = existing.trim() ? tags + ', ' + existing : tags;
        }
        else if (insertMode == 'append') {
            promptBox.value = existing.trim() ? existing + ', ' + tags : tags;
        }
        else {
            promptBox.value = tags;
        }

        triggerChangeFor(promptBox);
        promptBox.focus();
        promptBox.setSelectionRange(0, promptBox.value.length);
    }

    /**
     * Opens the interactive PixAI Studio modal and loads the specified image.
     * @param {string} src - Image source URL or data URI.
     */
    async openStudioWithImage(src) {
        this.openStudio();
        if (src) {
            let base64 = await this.getImageBase64(src);
            if (base64) {
                this.loadImageIntoStudio(base64, src);
            }
        }
    }

    /**
     * Loads an image into the interactive Studio UI.
     * @param {string} base64 - Base64 image payload.
     * @param {string} previewSrc - Image source URL for preview rendering.
     */
    loadImageIntoStudio(base64, previewSrc) {
        this.currentImageBase64 = base64;
        let previewImg = document.getElementById('pixai_studio_preview_img');
        let previewWrap = document.getElementById('pixai_studio_preview_wrap');
        let dropzone = document.getElementById('pixai_studio_dropzone');
        let tagBtn = document.getElementById('pixai_studio_run_btn');

        if (previewImg && previewWrap) {
            previewImg.src = previewSrc || ('data:image/png;base64,' + base64);
            previewWrap.style.display = 'flex';
        }
        if (dropzone) {
            dropzone.style.display = 'none';
        }
        if (tagBtn) {
            tagBtn.disabled = false;
        }
    }

    /**
     * Clears the current studio image and resets the dropzone.
     */
    resetStudioImage() {
        this.currentImageBase64 = null;
        let previewWrap = document.getElementById('pixai_studio_preview_wrap');
        let dropzone = document.getElementById('pixai_studio_dropzone');
        let tagBtn = document.getElementById('pixai_studio_run_btn');
        let resultsBox = document.getElementById('pixai_studio_results');

        if (previewWrap) {
            previewWrap.style.display = 'none';
        }
        if (dropzone) {
            dropzone.style.display = 'flex';
        }
        if (tagBtn) {
            tagBtn.disabled = true;
        }
        if (resultsBox) {
            resultsBox.style.display = 'none';
        }
    }

    /**
     * Constructs and opens the interactive PixAI Tagger Studio modal.
     */
    openStudio() {
        if (!this.modalElement) {
            this.buildStudioModal();
        }
        this.syncFromT2IParams();
        this.updateStudioUIFromState();
        if (!this.currentImageBase64) {
            let elem = document.getElementById('input_pixaiimage');
            if (elem) {
                let fileData = typeof getInputVal == 'function' ? getInputVal(elem) : null;
                if (!fileData) {
                    let previewImg = elem.closest('.auto-file-box')?.querySelector('.auto-input-preview img');
                    if (previewImg && previewImg.src) {
                        fileData = previewImg.src;
                    }
                }
                if (fileData) {
                    this.getImageBase64(fileData).then((b64) => {
                        if (b64 && !this.currentImageBase64) {
                            this.loadImageIntoStudio(b64, fileData);
                        }
                    });
                }
            }
        }
        this.modalElement.classList.add('show');
    }

    /**
     * Closes the interactive PixAI Tagger Studio modal.
     */
    closeStudio() {
        if (this.modalElement) {
            this.modalElement.classList.remove('show');
        }
    }

    /**
     * Synchronizes the studio slider and input values with the current helper state.
     */
    updateStudioUIFromState() {
        let cats = ['general', 'character', 'clothing', 'style', 'copyright'];
        for (let cat of cats) {
            let val = this.thresholds[cat];
            let isEnabled = !!this.categories[cat];

            let slider = document.getElementById('pixai_slider_' + cat);
            let input = document.getElementById('pixai_num_' + cat);
            let check = document.getElementById('pixai_studio_toggle_' + cat);
            let resetBtn = document.getElementById('pixai_reset_' + cat);
            let card = check ? check.closest('.pixai-tagger-card') : null;

            if (slider) {
                slider.value = val;
                slider.disabled = !isEnabled;
            }
            if (input) {
                input.value = Number(val).toFixed(2);
                input.disabled = !isEnabled;
            }
            if (resetBtn) {
                resetBtn.disabled = !isEnabled;
            }
            if (check) {
                check.checked = isEnabled;
            }
            if (card) {
                if (isEnabled) {
                    card.classList.remove('disabled-card');
                }
                else {
                    card.classList.add('disabled-card');
                }
            }
        }

        let confBox = document.getElementById('pixai_studio_check_conf');
        if (confBox) {
            confBox.checked = this.includeConfidence;
        }
        let underBox = document.getElementById('pixai_studio_check_under');
        if (underBox) {
            underBox.checked = this.keepUnderscores;
        }

        for (let cat of cats) {
            let pill = document.getElementById('pixai_pill_' + cat);
            if (pill) {
                let pillLabel = pill.dataset.label || cat;
                if (this.categories[cat]) {
                    pill.classList.add('active');
                    pill.textContent = '✓ ' + pillLabel;
                }
                else {
                    pill.classList.remove('active');
                    pill.textContent = pillLabel;
                }
            }
        }
    }

    /**
     * Builds the entire interactive Studio Modal with all threshold sliders, checkboxes, dropzone, and tags breakdown.
     */
    buildStudioModal() {
        let modal = document.createElement('div');
        modal.className = 'pixai-tagger-modal';
        modal.id = 'pixai_tagger_modal';

        let modalContent = document.createElement('div');
        modalContent.className = 'pixai-tagger-modal-content';

        // Modal Header
        let header = document.createElement('div');
        header.className = 'pixai-tagger-modal-header';

        let title = document.createElement('div');
        title.className = 'pixai-tagger-modal-title';
        title.innerHTML = '<span>🌸 PixAI Tagger v1.0 Studio (GPU)</span>';

        let closeBtn = document.createElement('button');
        closeBtn.className = 'pixai-tagger-modal-close';
        closeBtn.innerHTML = '&times;';
        closeBtn.onclick = () => this.closeStudio();

        header.appendChild(title);
        header.appendChild(closeBtn);
        modalContent.appendChild(header);

        // Subtitle / instructions text
        let desc = document.createElement('p');
        desc.className = 'pixai-tagger-title-desc';
        desc.textContent = 'Enable or disable each category threshold with the checkbox. Raise a threshold for fewer, more confident tags, or lower it to include more possibilities.';
        modalContent.appendChild(desc);

        // Sliders Grid
        let grid = document.createElement('div');
        grid.className = 'pixai-tagger-grid';

        let sliderSpecs = [
            { id: 'general', label: 'General threshold', pillLabel: 'General', def: this.defaults.general, isCloth: false },
            { id: 'character', label: 'Character threshold', pillLabel: 'Character', def: this.defaults.character, isCloth: false },
            { id: 'clothing', label: 'Clothing threshold', pillLabel: 'Clothing', def: this.defaults.clothing, isCloth: true },
            { id: 'style', label: 'Style threshold', pillLabel: 'Style', def: this.defaults.style, isCloth: false },
            { id: 'copyright', label: 'Copyright / series threshold', pillLabel: 'Copyright / series', def: this.defaults.copyright, isCloth: false }
        ];

        for (let spec of sliderSpecs) {
            let isEnabled = !!this.categories[spec.id];
            let card = document.createElement('div');
            card.className = 'pixai-tagger-card' + (isEnabled ? '' : ' disabled-card');

            let cardHeader = document.createElement('div');
            cardHeader.className = 'pixai-tagger-card-header';

            let headerLeft = document.createElement('div');
            headerLeft.className = 'pixai-tagger-card-header-left';

            let checkLabel = document.createElement('label');
            checkLabel.className = 'pixai-tagger-card-check-label';
            checkLabel.title = 'Enable or disable ' + spec.label;

            let check = document.createElement('input');
            check.type = 'checkbox';
            check.className = 'pixai-tagger-card-checkbox';
            check.id = 'pixai_studio_toggle_' + spec.id;
            check.checked = isEnabled;

            let badge = document.createElement('span');
            badge.className = 'pixai-tagger-badge' + (spec.isCloth ? ' pixai-tagger-badge-clothing' : '');
            badge.textContent = spec.label;

            checkLabel.appendChild(check);
            checkLabel.appendChild(badge);
            headerLeft.appendChild(checkLabel);

            let valBox = document.createElement('div');
            valBox.className = 'pixai-tagger-val-box';

            let numInput = document.createElement('input');
            numInput.type = 'number';
            numInput.className = 'pixai-tagger-num-input';
            numInput.id = 'pixai_num_' + spec.id;
            numInput.min = '0';
            numInput.max = '1';
            numInput.step = '0.01';
            numInput.value = Number(spec.def).toFixed(2);
            numInput.disabled = !isEnabled;

            let resetBtn = document.createElement('button');
            resetBtn.className = 'pixai-tagger-reset-btn';
            resetBtn.id = 'pixai_reset_' + spec.id;
            resetBtn.innerHTML = '&#8634;';
            resetBtn.title = 'Reset to default (' + spec.def + ')';
            resetBtn.disabled = !isEnabled;
            resetBtn.onclick = () => {
                this.thresholds[spec.id] = spec.def;
                numInput.value = Number(spec.def).toFixed(2);
                let slider = document.getElementById('pixai_slider_' + spec.id);
                if (slider) {
                    slider.value = spec.def;
                }
                this.syncToT2IParams();
            };

            valBox.appendChild(numInput);
            valBox.appendChild(resetBtn);
            cardHeader.appendChild(headerLeft);
            cardHeader.appendChild(valBox);

            let sliderContainer = document.createElement('div');
            sliderContainer.className = 'pixai-tagger-slider-container';

            let leftBound = document.createElement('span');
            leftBound.className = 'pixai-tagger-bound-label';
            leftBound.textContent = '0';

            let slider = document.createElement('input');
            slider.type = 'range';
            slider.className = 'pixai-tagger-slider';
            slider.id = 'pixai_slider_' + spec.id;
            slider.min = '0';
            slider.max = '1';
            slider.step = '0.01';
            slider.value = spec.def;
            slider.disabled = !isEnabled;

            let rightBound = document.createElement('span');
            rightBound.className = 'pixai-tagger-bound-label';
            rightBound.textContent = '1';

            check.onchange = () => {
                let checked = check.checked;
                this.categories[spec.id] = checked;
                slider.disabled = !checked;
                numInput.disabled = !checked;
                resetBtn.disabled = !checked;
                if (checked) {
                    card.classList.remove('disabled-card');
                }
                else {
                    card.classList.add('disabled-card');
                }
                let pill = document.getElementById('pixai_pill_' + spec.id);
                if (pill) {
                    if (checked) {
                        pill.classList.add('active');
                        pill.textContent = '✓ ' + spec.pillLabel;
                    }
                    else {
                        pill.classList.remove('active');
                        pill.textContent = spec.pillLabel;
                    }
                }
                this.syncToT2IParams();
            };

            slider.oninput = () => {
                let v = parseFloat(slider.value) || 0;
                this.thresholds[spec.id] = v;
                numInput.value = Number(v).toFixed(2);
                this.syncToT2IParams();
            };

            numInput.onchange = () => {
                let v = parseFloat(numInput.value);
                if (isNaN(v)) {
                    v = spec.def;
                }
                v = Math.max(0, Math.min(1, v));
                this.thresholds[spec.id] = v;
                slider.value = v;
                numInput.value = Number(v).toFixed(2);
                this.syncToT2IParams();
            };

            sliderContainer.appendChild(leftBound);
            sliderContainer.appendChild(slider);
            sliderContainer.appendChild(rightBound);

            card.appendChild(cardHeader);
            card.appendChild(sliderContainer);
            grid.appendChild(card);
        }

        modalContent.appendChild(grid);

        // Options Row (Confidence, Keep Underscores)
        let optionsRow = document.createElement('div');
        optionsRow.className = 'pixai-tagger-options-row';

        let confLabel = document.createElement('label');
        confLabel.className = 'pixai-tagger-checkbox-label';
        let confBox = document.createElement('input');
        confBox.type = 'checkbox';
        confBox.id = 'pixai_studio_check_conf';
        confBox.onchange = () => {
            this.includeConfidence = confBox.checked;
            this.syncToT2IParams();
        };
        confLabel.appendChild(confBox);
        confLabel.appendChild(document.createTextNode('Include confidence scores'));

        let underLabel = document.createElement('label');
        underLabel.className = 'pixai-tagger-checkbox-label';
        let underBox = document.createElement('input');
        underBox.type = 'checkbox';
        underBox.id = 'pixai_studio_check_under';
        underBox.onchange = () => {
            this.keepUnderscores = underBox.checked;
            this.syncToT2IParams();
        };
        underLabel.appendChild(underBox);
        underLabel.appendChild(document.createTextNode('Keep underscores in tag names'));

        optionsRow.appendChild(confLabel);
        optionsRow.appendChild(underLabel);
        modalContent.appendChild(optionsRow);

        // Categories in Combined Tags Pills
        let catSection = document.createElement('div');
        catSection.className = 'pixai-tagger-categories-section';

        let catHeader = document.createElement('div');
        catHeader.className = 'pixai-tagger-badge';
        catHeader.textContent = 'Categories in combined tags';
        catSection.appendChild(catHeader);

        let pillsRow = document.createElement('div');
        pillsRow.className = 'pixai-tagger-pills-row';

        let pillSpecs = [
            { id: 'character', label: 'Character' },
            { id: 'copyright', label: 'Copyright / series' },
            { id: 'style', label: 'Style' },
            { id: 'clothing', label: 'Clothing' },
            { id: 'general', label: 'General' }
        ];

        for (let p of pillSpecs) {
            let pill = document.createElement('button');
            pill.type = 'button';
            pill.className = 'pixai-tagger-pill' + (this.categories[p.id] ? ' active' : '');
            pill.id = 'pixai_pill_' + p.id;
            pill.dataset.label = p.label;
            pill.textContent = (this.categories[p.id] ? '✓ ' : '') + p.label;
            pill.onclick = () => {
                this.categories[p.id] = !this.categories[p.id];
                let checked = this.categories[p.id];
                if (checked) {
                    pill.classList.add('active');
                    pill.textContent = '✓ ' + p.label;
                }
                else {
                    pill.classList.remove('active');
                    pill.textContent = p.label;
                }

                let check = document.getElementById('pixai_studio_toggle_' + p.id);
                let slider = document.getElementById('pixai_slider_' + p.id);
                let numInput = document.getElementById('pixai_num_' + p.id);
                let resetBtn = document.getElementById('pixai_reset_' + p.id);
                if (check) {
                    check.checked = checked;
                    let cardElem = check.closest('.pixai-tagger-card');
                    if (cardElem) {
                        if (checked) {
                            cardElem.classList.remove('disabled-card');
                        }
                        else {
                            cardElem.classList.add('disabled-card');
                        }
                    }
                }
                if (slider) {
                    slider.disabled = !checked;
                }
                if (numInput) {
                    numInput.disabled = !checked;
                }
                if (resetBtn) {
                    resetBtn.disabled = !checked;
                }
                this.syncToT2IParams();
            };
            pillsRow.appendChild(pill);
        }
        catSection.appendChild(pillsRow);
        modalContent.appendChild(catSection);

        // Drag and Drop Zone
        let dropzone = document.createElement('div');
        dropzone.className = 'pixai-tagger-dropzone';
        dropzone.id = 'pixai_studio_dropzone';

        let fileInput = document.createElement('input');
        fileInput.type = 'file';
        fileInput.accept = 'image/*';
        fileInput.style.display = 'none';
        fileInput.onchange = (e) => {
            if (e.target.files && e.target.files[0]) {
                let file = e.target.files[0];
                let reader = new FileReader();
                reader.onload = (ev) => {
                    let b64 = ev.target.result.split(',')[1];
                    this.loadImageIntoStudio(b64, ev.target.result);
                };
                reader.readAsDataURL(file);
            }
        };

        dropzone.onclick = () => fileInput.click();

        dropzone.ondragover = (e) => {
            e.preventDefault();
            dropzone.classList.add('dragover');
        };
        dropzone.ondragleave = () => {
            dropzone.classList.remove('dragover');
        };
        dropzone.ondrop = (e) => {
            e.preventDefault();
            dropzone.classList.remove('dragover');
            if (e.dataTransfer.files && e.dataTransfer.files[0]) {
                let file = e.dataTransfer.files[0];
                if (file.type.startsWith('image/')) {
                    let reader = new FileReader();
                    reader.onload = (ev) => {
                        let b64 = ev.target.result.split(',')[1];
                        this.loadImageIntoStudio(b64, ev.target.result);
                    };
                    reader.readAsDataURL(file);
                }
            }
        };

        let dzIcon = document.createElement('div');
        dzIcon.className = 'pixai-tagger-dropzone-icon';
        dzIcon.textContent = '🖼️';

        let dzText = document.createElement('div');
        dzText.className = 'pixai-tagger-dropzone-text';
        dzText.textContent = 'Drag & drop an image here, click to browse, or paste (Ctrl+V)';

        let dzSub = document.createElement('div');
        dzSub.className = 'pixai-tagger-dropzone-subtext';
        dzSub.textContent = 'Supports PNG, JPG, WEBP • Processed directly on GPU';

        dropzone.appendChild(fileInput);
        dropzone.appendChild(dzIcon);
        dropzone.appendChild(dzText);
        dropzone.appendChild(dzSub);
        modalContent.appendChild(dropzone);

        // Image Preview & Run Action Bar
        let previewWrap = document.createElement('div');
        previewWrap.className = 'pixai-tagger-preview-wrapper';
        previewWrap.id = 'pixai_studio_preview_wrap';
        previewWrap.style.display = 'none';

        let previewImg = document.createElement('img');
        previewImg.className = 'pixai-tagger-preview-img';
        previewImg.id = 'pixai_studio_preview_img';

        let previewInfo = document.createElement('div');
        previewInfo.className = 'pixai-tagger-preview-info';

        let runBtn = document.createElement('button');
        runBtn.type = 'button';
        runBtn.className = 'pixai-tagger-action-btn';
        runBtn.id = 'pixai_studio_run_btn';
        runBtn.innerHTML = '<span>🌸 Tag Image on GPU</span>';
        runBtn.onclick = () => this.runStudioTagging();

        let changeImgBtn = document.createElement('button');
        changeImgBtn.type = 'button';
        changeImgBtn.className = 'pixai-tagger-sec-btn';
        changeImgBtn.textContent = 'Select Different Image';
        changeImgBtn.onclick = () => this.resetStudioImage();

        previewInfo.appendChild(runBtn);
        previewInfo.appendChild(changeImgBtn);
        previewWrap.appendChild(previewImg);
        previewWrap.appendChild(previewInfo);
        modalContent.appendChild(previewWrap);

        // Results Section
        let resultsBox = document.createElement('div');
        resultsBox.className = 'pixai-tagger-results';
        resultsBox.id = 'pixai_studio_results';
        resultsBox.style.display = 'none';

        let promptBox = document.createElement('textarea');
        promptBox.className = 'pixai-tagger-prompt-box';
        promptBox.id = 'pixai_studio_prompt_output';
        promptBox.placeholder = 'Generated tags will appear here...';

        let actionsRow = document.createElement('div');
        actionsRow.className = 'pixai-tagger-results-actions';

        let copyBtn = document.createElement('button');
        copyBtn.className = 'pixai-tagger-sec-btn';
        copyBtn.textContent = '📋 Copy Tags';
        copyBtn.onclick = () => {
            navigator.clipboard.writeText(promptBox.value);
            copyBtn.textContent = '✓ Copied!';
            setTimeout(() => { copyBtn.textContent = '📋 Copy Tags'; }, 1500);
        };

        let sendPosBtn = document.createElement('button');
        sendPosBtn.className = 'pixai-tagger-sec-btn';
        sendPosBtn.textContent = '⬇️ Replace Prompt';
        sendPosBtn.onclick = () => {
            this.insertTagsIntoPrompt(promptBox.value, 'replace');
            sendPosBtn.textContent = '✓ Sent!';
            setTimeout(() => { sendPosBtn.textContent = '⬇️ Replace Prompt'; }, 1500);
        };

        let prependBtn = document.createElement('button');
        prependBtn.className = 'pixai-tagger-sec-btn';
        prependBtn.textContent = '➕ Prepend';
        prependBtn.onclick = () => {
            this.insertTagsIntoPrompt(promptBox.value, 'prepend');
            prependBtn.textContent = '✓ Prepended!';
            setTimeout(() => { prependBtn.textContent = '➕ Prepend'; }, 1500);
        };

        let appendBtn = document.createElement('button');
        appendBtn.className = 'pixai-tagger-sec-btn';
        appendBtn.textContent = '➕ Append';
        appendBtn.onclick = () => {
            this.insertTagsIntoPrompt(promptBox.value, 'append');
            appendBtn.textContent = '✓ Appended!';
            setTimeout(() => { appendBtn.textContent = '➕ Append'; }, 1500);
        };

        actionsRow.appendChild(copyBtn);
        actionsRow.appendChild(sendPosBtn);
        actionsRow.appendChild(prependBtn);
        actionsRow.appendChild(appendBtn);

        let breakdownContainer = document.createElement('div');
        breakdownContainer.className = 'pixai-tagger-categories-breakdown';
        breakdownContainer.id = 'pixai_studio_breakdown';

        resultsBox.appendChild(promptBox);
        resultsBox.appendChild(actionsRow);
        resultsBox.appendChild(breakdownContainer);
        modalContent.appendChild(resultsBox);

        modal.appendChild(modalContent);
        document.body.appendChild(modal);
        this.modalElement = modal;

        // Clipboard Paste Listener
        window.addEventListener('paste', (e) => {
            if (!this.modalElement || !this.modalElement.classList.contains('show')) {
                return;
            }
            if (e.clipboardData && e.clipboardData.items) {
                for (let item of e.clipboardData.items) {
                    if (item.type.startsWith('image/')) {
                        let file = item.getAsFile();
                        let reader = new FileReader();
                        reader.onload = (ev) => {
                            let b64 = ev.target.result.split(',')[1];
                            this.loadImageIntoStudio(b64, ev.target.result);
                        };
                        reader.readAsDataURL(file);
                        break;
                    }
                }
            }
        });
    }

    /**
     * Executes tagging from the Studio UI.
     */
    async runStudioTagging() {
        if (!this.currentImageBase64) {
            showError('PixAI Tagger: Please provide an image first.');
            return;
        }

        let runBtn = document.getElementById('pixai_studio_run_btn');
        let promptBox = document.getElementById('pixai_studio_prompt_output');
        let resultsBox = document.getElementById('pixai_studio_results');
        let breakdown = document.getElementById('pixai_studio_breakdown');

        if (runBtn) {
            runBtn.disabled = true;
            runBtn.innerHTML = '<span>⏳ Tagging on GPU...</span>';
        }

        try {
            let result = await this.executeTagging(this.currentImageBase64);
            this.lastResult = result;

            if (resultsBox) {
                resultsBox.style.display = 'flex';
            }
            if (promptBox) {
                promptBox.value = result.tags || '';
            }

            if (breakdown && result.details) {
                breakdown.innerHTML = '';
                let catOrder = [
                    { key: 'character', label: '🏷️ Character', color: '#ff77aa' },
                    { key: 'copyright', label: '🏷️ Copyright / Series', color: '#bb77ff' },
                    { key: 'style', label: '🏷️ Style', color: '#77aaff' },
                    { key: 'clothing', label: '👗 Clothing & Attire', color: '#ffaa44' },
                    { key: 'general', label: '🏷️ General', color: '#77ddaa' }
                ];

                for (let cat of catOrder) {
                    let items = result.details[cat.key] || [];
                    if (items.length > 0) {
                        let group = document.createElement('div');
                        group.className = 'pixai-tagger-cat-group';

                        let title = document.createElement('div');
                        title.className = 'pixai-tagger-cat-title';
                        title.style.color = cat.color;
                        title.textContent = cat.label + ' (' + items.length + ')';

                        let tagFlow = document.createElement('div');
                        tagFlow.className = 'pixai-tagger-tags-flow';

                        for (let item of items) {
                            let chip = document.createElement('span');
                            chip.className = 'pixai-tag-chip';
                            chip.title = 'Click to copy "' + item.tag + '"';
                            chip.innerHTML = escapeHtml(item.tag) + ' <span class="pixai-tag-score">' + Math.round(item.score * 100) + '%</span>';
                            chip.onclick = () => {
                                navigator.clipboard.writeText(item.tag);
                                chip.style.borderColor = '#00ff88';
                                setTimeout(() => { chip.style.borderColor = ''; }, 1000);
                            };
                            tagFlow.appendChild(chip);
                        }

                        group.appendChild(title);
                        group.appendChild(tagFlow);
                        breakdown.appendChild(group);
                    }
                }
            }
        }
        catch (err) {
            showError('PixAI Tagger: ' + err.message);
        }
        finally {
            if (runBtn) {
                runBtn.disabled = false;
                runBtn.innerHTML = '<span>🌸 Tag Image on GPU</span>';
            }
        }
    }
}

let pixaiTagger = new PixaiTaggerHelper();

// Register media buttons for image viewer
setTimeout(() => {
    if (typeof registerMediaButton == 'function') {
        registerMediaButton(
            '🌸 PixAI Tag',
            (src) => pixaiTagger.handleViewerTag(src),
            'Tag this image with PixAI Tagger v1.0 on GPU and send tags to the prompt',
            ['image'],
            true,
            true
        );

        registerMediaButton(
            '⚙️ PixAI Studio',
            (src) => pixaiTagger.openStudioWithImage(src),
            'Open PixAI Tagger Studio with sliders and clothing threshold controls',
            ['image'],
            false,
            true
        );
    }

    if (typeof promptTabComplete != 'undefined') {
        promptTabComplete.registerPrefix('pixaitagger', 'Auto-tag [PixAI] Image or Init Image with PixAI Tagger v1.0 on GPU.\nUsage: <pixaitagger> or <pixaitagger:general,character,clothing>', (prefix) => {
            return [
                '\nAdd "<pixaitagger>" anywhere in your prompt to tag [PixAI] Image (or Init Image) on GPU.',
                '\nOptional overrides: "<pixaitagger:general_threshold,character_threshold,clothing_threshold>".',
                '\nExample: "<pixaitagger:0.17,0.27,0.17>".'
            ];
        }, true);
    }
}, 0);

/**
 * Sets initial default toggle states for PixAI threshold parameters in SwarmUI.
 * By default: General, Character, and Clothing are ON; Style and Copyright are OFF.
 * Remembers user preference across reloads via localStorage and SwarmUI cookies.
 */
function initPixaiDefaultToggles() {
    let defaultsOn = ['pixaigeneralthreshold', 'pixaicharacterthreshold', 'pixaiclothingthreshold'];
    let defaultsOff = ['pixaistylethreshold', 'pixaicopyrightseriesthreshold'];

    for (let id of defaultsOn) {
        let toggle = document.getElementById('input_' + id + '_toggle');
        if (!toggle) {
            continue;
        }

        let saved = localStorage.getItem('pixai_toggle_' + id);
        let shouldBeOn = true;
        if (saved == 'false') {
            shouldBeOn = false;
        }

        if (toggle.checked != shouldBeOn) {
            toggle.checked = shouldBeOn;
            if (typeof doToggleEnable == 'function') {
                doToggleEnable('input_' + id);
            }
        }

        if (shouldBeOn) {
            let days = typeof getParamMemoryDays == 'function' ? getParamMemoryDays() : 1000;
            setCookie('lastparam_input_' + id + '_toggle', 'true', days);
            let range = document.getElementById('input_' + id + '_rangeslider');
            if (range && typeof updateRangeStyle == 'function') {
                updateRangeStyle(range);
            }
        }

        if (!toggle.dataset.pixaiTracked) {
            toggle.dataset.pixaiTracked = 'true';
            toggle.addEventListener('change', () => {
                localStorage.setItem('pixai_toggle_' + id, toggle.checked ? 'true' : 'false');
            });
        }
    }

    for (let id of defaultsOff) {
        let toggle = document.getElementById('input_' + id + '_toggle');
        if (!toggle) {
            continue;
        }

        let saved = localStorage.getItem('pixai_toggle_' + id);
        let shouldBeOn = false;
        if (saved == 'true') {
            shouldBeOn = true;
        }

        if (toggle.checked != shouldBeOn) {
            toggle.checked = shouldBeOn;
            if (typeof doToggleEnable == 'function') {
                doToggleEnable('input_' + id);
            }
        }

        if (shouldBeOn) {
            let days = typeof getParamMemoryDays == 'function' ? getParamMemoryDays() : 1000;
            setCookie('lastparam_input_' + id + '_toggle', 'true', days);
            let range = document.getElementById('input_' + id + '_rangeslider');
            if (range && typeof updateRangeStyle == 'function') {
                updateRangeStyle(range);
            }
        }

        if (!toggle.dataset.pixaiTracked) {
            toggle.dataset.pixaiTracked = 'true';
            toggle.addEventListener('change', () => {
                localStorage.setItem('pixai_toggle_' + id, toggle.checked ? 'true' : 'false');
            });
        }
    }
}

if (typeof postParamBuildSteps !== 'undefined') {
    postParamBuildSteps.push(initPixaiDefaultToggles);
    postParamBuildSteps.push(initPixaiImageInput);
}
setTimeout(initPixaiDefaultToggles, 100);
setTimeout(initPixaiDefaultToggles, 350);
setTimeout(initPixaiDefaultToggles, 1000);
setTimeout(initPixaiImageInput, 150);
setTimeout(initPixaiImageInput, 400);
setTimeout(initPixaiImageInput, 1000);

/**
 * Injects a 'Tag Uploaded Image' button into the [PixAI] Image parameter widget if present.
 */
function initPixaiImageInput() {
    let inputElem = document.getElementById('input_pixaiimage');
    if (!inputElem) {
        return;
    }
    let parent = inputElem.closest('.auto-file-box') || (typeof findParentOfClass == 'function' ? findParentOfClass(inputElem, 'auto-file-box') : inputElem.parentElement);
    if (!parent || parent.querySelector('.pixai-tag-image-action-btn')) {
        return;
    }
    let tagBtn = document.createElement('button');
    tagBtn.type = 'button';
    tagBtn.className = 'basic-button pixai-tag-image-action-btn';
    tagBtn.style.marginTop = '6px';
    tagBtn.style.width = '100%';
    tagBtn.innerHTML = '<span>🌸 Tag Uploaded Image</span>';
    tagBtn.title = 'Run PixAI Tagger on this image on GPU and send tags to the prompt box';
    tagBtn.onclick = () => {
        let fileData = typeof getInputVal == 'function' ? getInputVal(inputElem) : null;
        if (!fileData) {
            let previewImg = parent.querySelector('.auto-input-preview img');
            if (previewImg && previewImg.src) {
                fileData = previewImg.src;
            }
        }
        if (!fileData) {
            showError('PixAI Tagger: Please upload, paste, or select an image in [PixAI] Image first.');
            return;
        }
        pixaiTagger.handleViewerTag(fileData);
    };
    parent.appendChild(tagBtn);
}
