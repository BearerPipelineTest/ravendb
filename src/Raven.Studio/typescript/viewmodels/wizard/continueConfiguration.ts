import setupStep = require("viewmodels/wizard/setupStep");
import router = require("plugins/router");

class continueConfiguration extends setupStep {

     canActivate(): JQueryPromise<canActivateResultDto> {
        const mode = this.model.mode();

        if (mode && mode === "Continue") {
            return $.when({ can: true });
        }

        return $.when({ redirect: "#welcome" });
    }
    
    
    back() {
        router.navigate("#welcome");
    }
    
    save() {
        if (!this.model.continueSetup().isZipFileSecure()) {
            this.model.registerClientCertificate(false);
        }
            
        if (this.isValid(this.model.continueSetup().validationGroup)) {
            router.navigate("#finish");
        }
    }
}

export = continueConfiguration;
