﻿import searchDialogViewModel = require("viewmodels/filesystem/searchDialogViewModel");
import datePickerBindingHandler = require("common/datePickerBindingHandler");
import moment = require("moment");
import dialog = require("plugins/dialog");

class fieldRangeFilter extends searchDialogViewModel {
    filterOptions = ko.observableArray(["Numeric Double", "Numeric Int", "Alphabetical", "Datetime"]);
    selectedOption = ko.observable("Starts with");
    public applyFilterTask = $.Deferred();
    label = "";
    from = ko.observable();
    to = ko.observable();
    fromDate = ko.observable<Moment>();
    toDate = ko.observable<Moment>();
    constructor(label: string) {
        super([ko.observable("")]);
        datePickerBindingHandler.install();
        this.label = label;
        this.from("");
        this.to("");
        this.fromDate.subscribe(v =>
            this.from(this.fromDate() != null ? this.fromDate().format("YYYY-MM-DDTHH:mm:00.0000000") : ""));

        this.toDate.subscribe(v =>
            this.to(this.toDate() != null ? this.toDate().format("YYYY-MM-DDTHH:mm:00.0000000") : ""));
    }

    applyFilter() {
        this.applyFilterTask.resolve(this.from(), this.to(), this.selectedOption());

        this.close();
    }

    enabled(): boolean {
        return true;
    }

    isDateTime(): boolean {
        return (this.selectedOption() === "Datetime");
    }
}

export = fieldRangeFilter;  