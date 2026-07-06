-- QuerySpace In-Memory object explorer test script
-- How to test:
-- 1. Open QuerySpace.
-- 2. Set Use = Memory.
-- 3. Paste/run this script in the Query panel.
-- 4. Open the In-Memory tab.
-- 5. Confirm Tables, Views, Macros / Functions, Indexes, Sequences, and Schemas appear.

drop view if exists vw_qs_missing_materials;
drop table if exists qs_material_stage;
drop macro if exists qs_normalize_material;
drop sequence if exists qs_batch_seq;
create schema if not exists qs_demo_schema;
drop table if exists qs_demo_schema.qs_schema_table;
drop schema if exists qs_demo_schema;

create table qs_material_stage as
select *
from (
    values
        (' mat-001 ', '1000', 'ECC', 10.5),
        ('MAT-002', '1000', 'ECC', 20.0),
        ('mat-003', '2000', 'ECC', null),
        (null, '2000', 'ECC', 5.0)
) as t(material_number, plant, source_system, quantity);

create macro qs_normalize_material(x) as
case
    when x is null or trim(x) = '' then null
    else upper(regexp_replace(trim(x), '[^A-Za-z0-9]', '', 'g'))
end;

create view vw_qs_missing_materials as
select
    qs_normalize_material(material_number) as normalized_material,
    plant,
    source_system,
    quantity
from qs_material_stage
where material_number is null or quantity is null;

create index idx_qs_material_stage_material
on qs_material_stage(material_number);

create sequence qs_batch_seq start 100 increment 1;

create schema qs_demo_schema;

create table qs_demo_schema.qs_schema_table as
select
    nextval('qs_batch_seq') as batch_id,
    qs_normalize_material(material_number) as normalized_material,
    plant
from qs_material_stage;

select * from qs_material_stage;
