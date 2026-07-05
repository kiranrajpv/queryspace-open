-- QuerySpace Template: Extract
-- Purpose: Base query used in Extract Base Query to pull ECC/SAP source data into a local Parquet dataset.
--
-- Tags:
-- @template: extract
-- @phase: extract
-- @analysis: sap-material-transformation
-- @object: material-master
-- @source: ECC
-- @extract: ecc_material_extract
-- @source-system: SAP ECC
-- @target: MDG
-- @postload: s4_mdg_material
-- @xref: material_cross_reference
-- @keys: source_matnr,source_werks
-- @business-date: 2026-07-04
--
-- How to use:
-- 1. Replace table names and filters with your ECC source tables.
-- 2. Keep source aliases prefixed with source_ when this extract will be used later for Analyze Transform.
-- 3. Run this through Extract Base Query and save as a unique dataset name, for example ecc_material_extract.

select
    mara.matnr                         as source_matnr,
    marc.werks                         as source_werks,
    mara.mtart                         as source_material_type,
    makt.maktx                         as source_material_description,
    mara.meins                         as source_base_uom,
    marc.dispo                         as source_mrp_controller,
    marc.beskz                         as source_procurement_type,
    mara.ersda                         as source_created_on,
    current_timestamp                  as source_extract_timestamp
from mara
left join makt
    on makt.matnr = mara.matnr
   and makt.spras = 'E'
left join marc
    on marc.matnr = mara.matnr
where mara.mandt = '100'
  and mara.lvorm <> 'X'
  and marc.werks in ('1000', '1100')
;
