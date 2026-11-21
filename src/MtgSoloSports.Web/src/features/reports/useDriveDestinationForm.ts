import { useCallback, useState } from 'react';
import type { DriveStatus } from './drivePublishingApi';

/** Editable Drive destination form: ids and names adopted from the loaded status. */
export interface DriveDestinationFormState {
  mtshistoryFolderId: string;
  mtshistoryFolderName: string;
  seasonsFolderId: string;
  dossierFileId: string;
  dossierFileName: string;
  almanacFileId: string;
  almanacFileName: string;
  colorCupFolderId: string;
  colorCupsFolderId: string;
  colorTeamsFolderId: string;
  typeCupFolderId: string;
  typeCupsFolderId: string;
  typeTeamsFolderId: string;
  chaosCupFolderId: string;
  chaosCupsFolderId: string;
  athletesFolderId: string;
  athletesFolderName: string;
  athletesIndexFileId: string;
  athletesIndexFileName: string;
}

const EMPTY_FORM: DriveDestinationFormState = {
  mtshistoryFolderId: '',
  mtshistoryFolderName: '',
  seasonsFolderId: '',
  dossierFileId: '',
  dossierFileName: '',
  almanacFileId: '',
  almanacFileName: '',
  colorCupFolderId: '',
  colorCupsFolderId: '',
  colorTeamsFolderId: '',
  typeCupFolderId: '',
  typeCupsFolderId: '',
  typeTeamsFolderId: '',
  chaosCupFolderId: '',
  chaosCupsFolderId: '',
  athletesFolderId: '',
  athletesFolderName: '',
  athletesIndexFileId: '',
  athletesIndexFileName: '',
};

function formFromStatus(status: DriveStatus): DriveDestinationFormState {
  return {
    mtshistoryFolderId: status.mtshistoryFolderId ?? '',
    mtshistoryFolderName: status.mtshistoryFolderName ?? '',
    seasonsFolderId: status.seasonsFolderId ?? '',
    dossierFileId: status.dossier.fileId ?? '',
    dossierFileName: status.dossier.fileName ?? '',
    almanacFileId: status.almanac.fileId ?? '',
    almanacFileName: status.almanac.fileName ?? '',
    colorCupFolderId: status.colorCupFolderId ?? '',
    colorCupsFolderId: status.colorCupsFolderId ?? '',
    colorTeamsFolderId: status.colorTeamsFolderId ?? '',
    typeCupFolderId: status.typeCupFolderId ?? '',
    typeCupsFolderId: status.typeCupsFolderId ?? '',
    typeTeamsFolderId: status.typeTeamsFolderId ?? '',
    chaosCupFolderId: status.chaosCupFolderId ?? '',
    chaosCupsFolderId: status.chaosCupsFolderId ?? '',
    athletesFolderId: status.athletesFolderId ?? '',
    athletesFolderName: status.athletesFolderName ?? '',
    athletesIndexFileId: status.athletesIndexFileId ?? '',
    athletesIndexFileName: status.athletesIndexFileName ?? '',
  };
}

/** Destination text fields for the Drive publishing card, loaded from status and edited by hand. */
export function useDriveDestinationForm() {
  const [form, setForm] = useState<DriveDestinationFormState>(EMPTY_FORM);

  const applyStatus = useCallback((status: DriveStatus) => {
    setForm(formFromStatus(status));
  }, []);

  const setField = useCallback((field: keyof DriveDestinationFormState, value: string) => {
    setForm((current) => ({ ...current, [field]: value }));
  }, []);

  return { form, setField, applyStatus };
}
